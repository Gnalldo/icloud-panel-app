# mailfeed.ps1 - mail + calendar source for the Calendar and Inbox widgets.
#
# Replaces the classic-Outlook COM helper. Nothing here needs Outlook, Office or a
# logged-on desktop session, so the data keeps flowing while the PC is gaming or
# asleep, and no machine has to stay signed in just to feed a widget.
#
#   you@outlook.com -> Microsoft Graph, device-code sign-in, no client secret
#   you@gmail.com   -> IMAP over TLS with a Google app password
#
# Prints one JSON object on stdout in the shape the panel already renders:
#   { ok, accounts[], unread, mail[], calendar[], at }   (+ errors[] when partial)
#
# Secrets never sit in the config: the Graph refresh token and the Gmail app
# password are stored DPAPI-encrypted for this Windows user in
# mailfeed-secrets.xml, which cannot be read on another account or machine.
#
# Usage
#   mailfeed.ps1                         fetch and print (what the panel runs)
#   mailfeed.ps1 -ClientId <guid>        save the Azure app client id
#   mailfeed.ps1 -SignIn                 device-code sign-in, stores the token
#   mailfeed.ps1 -GmailUser x -GmailPassword y   save the Gmail app password
#   mailfeed.ps1 -SelfTest               offline checks, no network, no secrets
#
# Read-only scopes: Mail.Read and Calendars.Read. No send, no delete.

param(
    [switch]$SignIn,
    [switch]$SelfTest,
    [string]$ClientId,
    [string]$GmailUser,
    [string]$GmailPassword,
    [int]$MailLimit = 8,
    [int]$CalLimit = 12,
    [int]$CalDays = 7
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$Here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$ConfigPath = Join-Path $Here 'mailfeed.json'
$SecretPath = Join-Path $Here 'mailfeed-secrets.xml'
$Authority  = 'https://login.microsoftonline.com/consumers'
$Scope      = 'offline_access Mail.Read Calendars.Read User.Read'
$Graph      = 'https://graph.microsoft.com/v1.0'

function Emit($obj) { $obj | ConvertTo-Json -Depth 8 -Compress }

# --------------------------------------------------------------- secrets ---
function Protect([string]$plain) {
    if ([string]::IsNullOrEmpty($plain)) { return '' }
    return (ConvertTo-SecureString $plain -AsPlainText -Force | ConvertFrom-SecureString)
}

function Unprotect([string]$enc) {
    if ([string]::IsNullOrEmpty($enc)) { return '' }
    try {
        $sec = ConvertTo-SecureString $enc
        return [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                 [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    } catch { return '' }
}

function Read-Secrets {
    $s = [pscustomobject]@{ GraphRefresh = ''; GmailPass = '' }
    if (Test-Path $SecretPath) {
        try {
            $raw = Import-Clixml $SecretPath
            if ($raw.GraphRefresh) { $s.GraphRefresh = [string]$raw.GraphRefresh }
            if ($raw.GmailPass)    { $s.GmailPass    = [string]$raw.GmailPass }
        } catch { }
    }
    return $s
}

function Save-Secrets($s) {
    $s | Export-Clixml -Path $SecretPath
}

function Read-Config {
    if (-not (Test-Path $ConfigPath)) { return $null }
    try { return (Get-Content $ConfigPath -Raw | ConvertFrom-Json) } catch { return $null }
}

# ------------------------------------------------------------ text utils ---
# RFC 2047 encoded words, e.g. =?UTF-8?B?SGVsbG8=?= or =?utf-8?Q?Caf=C3=A9?=
function Decode-MimeWord([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return '' }
    $text = [regex]::Replace($text, '(\?=)\s+(\=\?)', '$1$2')
    $out = [regex]::Replace($text, '=\?([^?]+)\?([BbQq])\?([^?]*)\?=', {
        param($m)
        $charset = $m.Groups[1].Value
        $enc     = $m.Groups[2].Value.ToUpperInvariant()
        $payload = $m.Groups[3].Value
        try {
            if ($enc -eq 'B') {
                $bytes = [Convert]::FromBase64String($payload)
            } else {
                $s = $payload -replace '_', ' '
                $bytes = New-Object System.Collections.Generic.List[byte]
                for ($i = 0; $i -lt $s.Length; $i++) {
                    if ($s[$i] -eq '=' -and ($i + 2) -lt $s.Length) {
                        $bytes.Add([Convert]::ToByte($s.Substring($i + 1, 2), 16))
                        $i += 2
                    } else {
                        $bytes.Add([byte][char]$s[$i])
                    }
                }
                $bytes = $bytes.ToArray()
            }
            $cs = $charset
            if ($cs -match '^(?i)utf-?8$') { $cs = 'utf-8' }
            if ($cs -match '^(?i)iso-8859-1$') { $cs = 'iso-8859-1' }
            return [Text.Encoding]::GetEncoding($cs).GetString($bytes)
        } catch {
            return $payload
        }
    })
    return $out
}

# Header block -> ordered name/value pairs, with folded continuation lines joined.
function Parse-Headers([string]$block) {
    $map = @{}
    $last = $null
    foreach ($line in ($block -split "`r`n")) {
        if ([string]::IsNullOrEmpty($line)) { continue }
        if ($line -match '^[ \t]' -and $last) {
            $map[$last] = ($map[$last] + ' ' + $line.Trim())
            continue
        }
        $idx = $line.IndexOf(':')
        if ($idx -lt 1) { continue }
        $name = $line.Substring(0, $idx).Trim().ToLowerInvariant()
        $map[$name] = $line.Substring($idx + 1).Trim()
        $last = $name
    }
    return $map
}

# "Name" <a@b> -> Name ; bare address -> address
function Parse-From([string]$value) {
    $value = Decode-MimeWord $value
    $m = [regex]::Match($value, '^\s*"?([^"<]*?)"?\s*<([^>]+)>\s*$')
    if ($m.Success) {
        $name = $m.Groups[1].Value.Trim()
        if ($name.Length -gt 0) { return $name }
        return $m.Groups[2].Value.Trim()
    }
    return $value.Trim()
}

function To-Iso([string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return '' }
    $v = $value.Trim()
    # A sender with the wrong day name (or none) must not cost the whole timestamp:
    # DateTime.Parse rejects "Mon, 10 Nov 2026" when that date is a Tuesday.
    $v = [regex]::Replace($v, '^[A-Za-z]{3},\s*', '')
    $cult   = [Globalization.CultureInfo]::InvariantCulture
    $styles = [Globalization.DateTimeStyles]::AllowWhiteSpaces -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    try { return ([datetime]::Parse($v, $cult, $styles)).ToString('o') } catch { }
    try { return ([datetime]::Parse($v, $cult, ($styles -bor [Globalization.DateTimeStyles]::AssumeUniversal))).ToString('o') } catch { }
    try {
        $m = [regex]::Match($v, '(\d{1,2})\s+([A-Za-z]{3})\s+(\d{4})\s+(\d{2}:\d{2}(:\d{2})?)')
        if ($m.Success) {
            $t = $m.Groups[4].Value
            if ($t.Length -eq 5) { $t = $t + ':00' }
            $s = $m.Groups[1].Value + ' ' + $m.Groups[2].Value + ' ' + $m.Groups[3].Value + ' ' + $t
            return ([datetime]::Parse($s, $cult, $styles)).ToString('o')
        }
    } catch { }
    return ''
}

# ---------------------------------------------------------------- graph ---
function Get-GraphToken($cfg, $secrets) {
    $rt = Unprotect $secrets.GraphRefresh
    if ([string]::IsNullOrEmpty($rt)) { throw 'no-token' }
    $body = @{
        client_id     = $cfg.clientId
        grant_type    = 'refresh_token'
        refresh_token = $rt
        scope         = $Scope
    }
    $r = Invoke-RestMethod -Method Post -Uri "$Authority/oauth2/v2.0/token" -Body $body -ErrorAction Stop
    if ($r.refresh_token) {
        # Microsoft rotates refresh tokens; keep the newest or the next run fails.
        $secrets.GraphRefresh = Protect ([string]$r.refresh_token)
        Save-Secrets $secrets
    }
    return [string]$r.access_token
}

function Invoke-GraphGet([string]$token, [string]$path, [string]$prefer) {
    $headers = @{ Authorization = "Bearer $token" }
    if ($prefer) { $headers['Prefer'] = $prefer }
    return Invoke-RestMethod -Method Get -Uri ($Graph + $path) -Headers $headers -ErrorAction Stop
}

function Get-OutlookFeed($cfg, $secrets) {
    $result = [pscustomobject]@{ ok = $false; error = 'graph-auth'; address = ''; unread = 0; mail = @(); calendar = @() }
    $token = $null
    try { $token = Get-GraphToken $cfg $secrets }
    catch { $result.error = 'graph-auth'; return $result }

    try {
        $me = Invoke-GraphGet $token '/me?$select=mail,userPrincipalName'
        if ($me.mail) { $result.address = [string]$me.mail }
        elseif ($me.userPrincipalName) { $result.address = [string]$me.userPrincipalName }
        else { $result.address = 'outlook' }
    } catch { $result.address = 'outlook'; $result.error = 'graph-failed'; return $result }

    try {
        $inbox = Invoke-GraphGet $token "/me/mailFolders('inbox')?`$select=unreadItemCount,displayName"
        $result.unread = [int]$inbox.unreadItemCount
    } catch { }

    try {
        $q = "/me/messages?`$top=$MailLimit&`$select=subject,from,receivedDateTime,isRead&`$orderby=receivedDateTime desc"
        $msgs = Invoke-GraphGet $token $q
        $items = @()
        foreach ($m in $msgs.value) {
            $from = ''
            if ($m.from -and $m.from.emailAddress) {
                $from = [string]$m.from.emailAddress.name
                if ([string]::IsNullOrEmpty($from)) { $from = [string]$m.from.emailAddress.address }
            }
            $items += [pscustomobject]@{
                account = $result.address
                from    = $from
                subject = [string]$m.subject
                time    = (To-Iso ([string]$m.receivedDateTime))
                unread  = (-not [bool]$m.isRead)
            }
        }
        $result.mail = @($items)
    } catch {
        # Mail but no calendar is still useful; keep whatever succeeded.
    }

    try {
        $all = ([Globalization.CultureInfo]::InvariantCulture)
        $start = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', $all)
        $end   = [datetime]::UtcNow.AddDays($CalDays).ToString('yyyy-MM-ddTHH:mm:ssZ', $all)
        $q = "/me/calendarView?startDateTime=$start&endDateTime=$end&`$top=$CalLimit&`$select=subject,start,isAllDay&`$orderby=start/dateTime"
        $evs = Invoke-GraphGet $token $q 'outlook.timezone="UTC"'
        $items = @()
        foreach ($e in $evs.value) {
            $when = ''
            if ($e.start -and $e.start.dateTime) { $when = To-Iso ([string]$e.start.dateTime) }
            if ([string]::IsNullOrEmpty($when)) { continue }
            $items += [pscustomobject]@{
                account = $result.address
                subject = [string]$e.subject
                time    = $when
                allday  = [bool]$e.isAllDay
            }
        }
        $result.calendar = @($items)
    } catch { }

    $result.ok = $true
    $result.error = ''
    return $result
}

# ----------------------------------------------------------------- gmail ---
function Read-ImapLine($sr) {
    $sb = New-Object System.Text.StringBuilder
    while ($true) {
        $ch = $sr.Read()
        if ($ch -lt 0) { return $null }
        if ($ch -eq 10) { break }
        if ($ch -eq 13) { continue }
        [void]$sb.Append([char]$ch)
    }
    return $sb.ToString()
}

# Reads until the tagged completion line, capturing literal payloads separately
# (a FETCH body is not line-structured, so it has to be read by octet count).
function Send-Imap($sw, $sr, [string]$tag, [string]$cmd) {
    $sw.Write($tag + ' ' + $cmd + "`r`n")
    $sw.Flush()
    $lines = New-Object System.Collections.ArrayList
    $lits  = New-Object System.Collections.ArrayList
    while ($true) {
        $line = Read-ImapLine $sr
        if ($null -eq $line) { throw 'imap-closed' }
        [void]$lines.Add($line)
        $m = [regex]::Match($line, '\{(\d+)\}$')
        if ($m.Success) {
            $n   = [int]$m.Groups[1].Value
            $buf = New-Object char[] $n
            $got = 0
            while ($got -lt $n) {
                $r = $sr.Read($buf, $got, $n - $got)
                if ($r -le 0) { throw 'imap-literal-short' }
                $got += $r
            }
            [void]$lits.Add(($buf -join ''))
            continue
        }
        if ($line.StartsWith($tag + ' ')) { break }
    }
    return @{ Lines = $lines; Literals = $lits }
}

function Quote-ImapString([string]$s) {
    return '"' + ($s -replace '\\', '\\\\' -replace '"', '\"') + '"'
}

function Get-GmailFeed([string]$user, [string]$password, [int]$limit) {
    $result = [pscustomobject]@{ ok = $false; error = 'gmail-auth'; unread = 0; mail = @(); calendar = @() }
    if ([string]::IsNullOrEmpty($user) -or [string]::IsNullOrEmpty($password)) { $result.error = 'gmail-config'; return $result }

    $tcp = $null; $ssl = $null; $sr = $null; $sw = $null
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect('imap.gmail.com', 993)
        $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream())
        $ssl.AuthenticateAsClient('imap.gmail.com')
        $sr = New-Object System.IO.StreamReader($ssl, [Text.Encoding]::ASCII)
        $sw = New-Object System.IO.StreamWriter($ssl, [Text.Encoding]::ASCII)
        $sw.NewLine = "`r`n"
        $sw.AutoFlush = $true

        $greet = Read-ImapLine $sr
        if ($null -eq $greet -or $greet -notmatch '^\* OK') { throw 'imap-greet' }

        $r = Send-Imap $sw $sr 'a1' ('LOGIN ' + (Quote-ImapString $user) + ' ' + (Quote-ImapString $password))
        if (-not ($r.Lines | Where-Object { $_ -match '^a1 OK' })) { throw 'imap-login' }

        $r = Send-Imap $sw $sr 'a2' 'STATUS "INBOX" (UNSEEN)'
        $um = [regex]::Match(($r.Lines -join ' '), 'UNSEEN\s+(\d+)')
        if ($um.Success) { $result.unread = [int]$um.Groups[1].Value }

        $r = Send-Imap $sw $sr 'a3' 'SELECT "INBOX"'
        $em = [regex]::Match(($r.Lines -join ' '), '\*\s+(\d+)\s+EXISTS')
        $total = 0
        if ($em.Success) { $total = [int]$em.Groups[1].Value }

        $items = @()
        if ($total -gt 0) {
            $first = [Math]::Max(1, $total - $limit + 1)
            for ($seq = $first; $seq -le $total; $seq++) {
                $tag = 'b' + $seq
                $r = Send-Imap $sw $sr $tag ("FETCH $seq (FLAGS BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])")
                $flagLine = ($r.Lines | Where-Object { $_ -match 'FETCH\s+\(' } | Select-Object -First 1)
                $seen = $false
                if ($flagLine -and $flagLine -match 'FLAGS\s+\(([^)]*)\)') { $seen = ($matches[1] -match '\\Seen') }
                $header = ''
                if ($r.Literals.Count -gt 0) { $header = [string]$r.Literals[0] }
                if ([string]::IsNullOrEmpty($header)) { continue }
                $h = Parse-Headers $header
                $items += [pscustomobject]@{
                    account = $user
                    from    = (Parse-From ([string]$h['from']))
                    subject = (Decode-MimeWord ([string]$h['subject']))
                    time    = (To-Iso ([string]$h['date']))
                    unread  = (-not $seen)
                }
            }
        }

        Send-Imap $sw $sr 'z9' 'LOGOUT' | Out-Null
        $result.mail = @($items)
        $result.ok = $true
        $result.error = ''
        return $result
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match 'imap-login' -or $msg -match 'AUTHENTICATIONFAILED' -or $msg -match 'Invalid credentials') {
            $result.error = 'gmail-auth'
        } else {
            $result.error = 'gmail-failed'
        }
        return $result
    } finally {
        foreach ($x in @($sw, $sr, $ssl, $tcp)) {
            if ($x) { try { $x.Dispose() } catch { } }
        }
    }
}

# -------------------------------------------------------------- envelope ---
function New-Envelope($outlook, $gmail) {
    $accounts = @()
    $mail     = @()
    $cal      = @()
    $unread   = 0
    $errors   = @()

    if ($outlook -and $outlook.ok) {
        $accounts += $outlook.address
        $unread   += $outlook.unread
        $mail     += @($outlook.mail)
        $cal      += @($outlook.calendar)
    } elseif ($outlook) { $errors += $outlook.error }

    if ($gmail -and $gmail.ok) {
        $accounts += $gmail.account
        $unread   += $gmail.unread
        $mail     += @($gmail.mail)
    } elseif ($gmail) { $errors += $gmail.error }

    $mail = @($mail | Sort-Object -Property time -Descending)
    $cal  = @($cal  | Sort-Object -Property time)

    $ok = (($null -ne $outlook -and $outlook.ok) -or ($null -ne $gmail -and $gmail.ok))
    $env = [pscustomobject]@{
        ok       = $ok
        accounts = @($accounts)
        unread   = $unread
        mail     = @($mail | Select-Object -First $MailLimit)
        calendar = @($cal  | Select-Object -First $CalLimit)
        at       = [datetime]::UtcNow.ToString('o')
    }
    if (-not $ok) {
        $env | Add-Member -NotePropertyName error -NotePropertyValue ($(if ($errors.Count -gt 0) { $errors[0] } else { 'no-config' }))
    } elseif ($errors.Count -gt 0) {
        $env | Add-Member -NotePropertyName errors -NotePropertyValue @($errors)
    }
    return $env
}

# ------------------------------------------------------------- self test ---
function Invoke-SelfTest {
    $fail = 0
    function Check([string]$name, [bool]$pass) {
        if ($pass) { Write-Output ("  ok   " + $name) }
        else { Write-Output ("  FAIL " + $name); $script:fail++ }
    }

    Check 'base64 encoded word' ((Decode-MimeWord '=?UTF-8?B?SGVsbG8=?=') -eq 'Hello')
    Check 'quoted-printable encoded word' ((Decode-MimeWord '=?utf-8?Q?Caf=C3=A9?=') -eq 'Caf' + [char]0xE9)
    Check 'plain subject untouched' ((Decode-MimeWord 'Re: lunch') -eq 'Re: lunch')

    $hdr = "From: ""Ada Lovelace"" <ada@example.com>`r`nSubject: =?UTF-8?B?SGVsbG8=?=`r`n`tcontinued`r`nDate: Tue, 10 Nov 2026 12:00:00 +0000`r`n"
    $h = Parse-Headers $hdr
    Check 'from display name' ((Parse-From ([string]$h['from'])) -eq 'Ada Lovelace')
    Check 'folded subject joined' ((Decode-MimeWord ([string]$h['subject'])) -eq 'Hello continued')
    Check 'date normalised' ((To-Iso ([string]$h['date'])) -like '2026-11-10T12:00:00*')
    Check 'wrong weekday tolerated' ((To-Iso 'Mon, 10 Nov 2026 12:00:00 +0000') -like '2026-11-10T12:00:00*')
    Check 'garbage date is empty' ((To-Iso 'not a date') -eq '')
    Check 'bare address from' ((Parse-From 'x@y.com') -eq 'x@y.com')

    $o = [pscustomobject]@{ ok = $true; address = 'a@b.com'; unread = 3; mail = @([pscustomobject]@{ account = 'a@b.com'; from = 'F'; subject = 'S'; time = '2026-11-10T10:00:00.0000000Z'; unread = $true }); calendar = @() }
    $g = [pscustomobject]@{ ok = $true; account = 'c@d.com'; unread = 2; mail = @([pscustomobject]@{ account = 'c@d.com'; from = 'G'; subject = 'T'; time = '2026-11-10T11:00:00.0000000Z'; unread = $false }) }
    $env = New-Envelope $o $g
    $json = Emit $env
    Check 'envelope ok' ($json -match '"ok":true')
    Check 'both accounts listed' ($env.accounts.Count -eq 2)
    Check 'unread summed' ($env.unread -eq 5)
    Check 'mail newest first' ($env.mail[0].from -eq 'G')

    $bad = New-Envelope $null ([pscustomobject]@{ ok = $false; error = 'gmail-auth'; unread = 0; mail = @() })
    Check 'failure envelope is not ok' ($bad.ok -eq $false)
    Check 'failure carries a code' ($bad.error -eq 'gmail-auth')

    if ($script:fail -gt 0) { throw ("self test failed: " + $script:fail) }
    Write-Output 'SELFTEST OK'
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

# ------------------------------------------------------------------ main ---
if ($ClientId -or $GmailUser -or $GmailPassword) {
    $cfg = Read-Config
    if (-not $cfg) { $cfg = [pscustomobject]@{ clientId = ''; gmail = [pscustomobject]@{ user = '' } } }
    if (-not $cfg.PSObject.Properties['gmail']) {
        $cfg | Add-Member -NotePropertyName gmail -NotePropertyValue ([pscustomobject]@{ user = '' })
    }
    if ($ClientId)     { $cfg.clientId = $ClientId }
    if ($GmailUser)    { $cfg.gmail.user = $GmailUser }
    $cfg | ConvertTo-Json -Depth 5 | Set-Content -Path $ConfigPath -Encoding ASCII

    if ($GmailPassword) {
        $s = Read-Secrets
        $s.GmailPass = Protect $GmailPassword
        Save-Secrets $s
        Write-Output 'gmail app password stored (encrypted for this Windows user)'
    }
    Write-Output ('config: ' + $ConfigPath)
    if (-not $SignIn) { exit 0 }
}

if ($SignIn) {
    $cfg = Read-Config
    if (-not $cfg -or [string]::IsNullOrEmpty($cfg.clientId)) { Write-Output 'set the client id first: -ClientId <guid>'; exit 1 }
    $dc = Invoke-RestMethod -Method Post -Uri "$Authority/oauth2/v2.0/devicecode" -Body @{
        client_id = $cfg.clientId
        scope     = $Scope
    } -ErrorAction Stop
    Write-Output ('open   ' + $dc.verification_uri)
    Write-Output ('code   ' + $dc.user_code)
    Write-Output 'waiting for approval...'
    $interval = 5
    if ($dc.interval) { $interval = [int]$dc.interval }
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            $t = Invoke-RestMethod -Method Post -Uri "$Authority/oauth2/v2.0/token" -Body @{
                client_id   = $cfg.clientId
                grant_type  = 'urn:ietf:params:oauth:grant-type:device_code'
                device_code = $dc.device_code
            } -ErrorAction Stop
        } catch {
            $detail = ''
            try { $detail = $_.ErrorDetails.Message } catch { }
            if ($detail -match 'authorization_pending') { continue }
            if ($detail -match 'slow_down') { $interval += 5; continue }
            Write-Output ('sign-in failed: ' + $detail)
            exit 1
        }
        if ($t.refresh_token) {
            $s = Read-Secrets
            $s.GraphRefresh = Protect ([string]$t.refresh_token)
            Save-Secrets $s
            Write-Output 'signed in, refresh token stored'
            exit 0
        }
    }
    Write-Output 'timed out waiting for approval'
    exit 1
}

$cfg = Read-Config
if (-not $cfg) { Emit ([pscustomobject]@{ ok = $false; error = 'no-config' }); exit 0 }

$secrets = Read-Secrets
$outlook = $null
$gmail   = $null

if (-not [string]::IsNullOrEmpty($cfg.clientId)) {
    $outlook = Get-OutlookFeed $cfg $secrets
}

$gmailUser = ''
if ($cfg.gmail) { $gmailUser = [string]$cfg.gmail.user }
if (-not [string]::IsNullOrEmpty($gmailUser)) {
    $g = Get-GmailFeed $gmailUser (Unprotect $secrets.GmailPass) $MailLimit
    if ($g.ok) {
        $g | Add-Member -NotePropertyName account -NotePropertyValue $gmailUser -Force
    } else {
        $g | Add-Member -NotePropertyName account -NotePropertyValue $gmailUser -Force
    }
    $gmail = $g
}

if ($null -eq $outlook -and $null -eq $gmail) {
    Emit ([pscustomobject]@{ ok = $false; error = 'no-config' })
    exit 0
}

Emit (New-Envelope $outlook $gmail)
exit 0

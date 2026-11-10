# outlook-widgets.ps1 - data source for the Calendar and Inbox widgets.
#
# Reads the next week of appointments and the newest messages from every account
# configured in classic Outlook, and prints them as one JSON object on stdout.
# Nothing is written to disk, so no mail content is ever stored anywhere.
#
# Classic Outlook only. New Outlook for Windows has no COM object model (OOM and
# MAPI are "not supported" there), and COM automation is not supported from a
# service either, so this has to run inside your own signed-in session.
#
# Read-only on purpose: no address-book or recipient lookups, which is what
# Outlook's Object Model Guard normally challenges.

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Emit($obj) {
    $obj | ConvertTo-Json -Depth 6 -Compress
}

$MAIL_LIMIT = 8
$CAL_LIMIT  = 12
$CAL_DAYS   = 7

# Launching Outlook just to fill a widget would be rude: report it and let the
# panel keep showing whatever it last had.
if (-not (Get-Process -Name outlook -ErrorAction SilentlyContinue)) {
    Emit @{ ok = $false; error = 'outlook-not-running' }
    exit 0
}

try {
    $ol = New-Object -ComObject Outlook.Application
    $ns = $ol.GetNamespace('MAPI')
} catch {
    Emit @{ ok = $false; error = ('com-failed: ' + $_.Exception.Message) }
    exit 0
}

$accounts = @()
$mail     = @()
$cal      = @()
$unread   = 0
$now      = Get-Date
$until    = $now.AddDays($CAL_DAYS)

foreach ($store in $ns.Stores) {
    $name = 'unknown'
    try { $name = [string]$store.DisplayName } catch { }
    $accounts += $name

    # ---------------------------------------------------------------- inbox ---
    # 6 = olFolderInbox. Each account is its own store, which is what lets one
    # pass cover Outlook.com and the Gmail account at the same time.
    try {
        $inbox = $store.GetDefaultFolder(6)
        if ($inbox) {
            try { $unread += [int]$inbox.UnReadItemCount } catch { }
            $items = $inbox.Items
            try { $items.Sort('[ReceivedTime]', $true) } catch { }
            $taken = 0
            for ($i = 1; $i -le $items.Count -and $taken -lt $MAIL_LIMIT; $i++) {
                $m = $null
                try { $m = $items.Item($i) } catch { }
                if ($null -eq $m) { continue }

                $subject  = ''
                $sender   = ''
                $received = $null
                $isUnread = $false
                try { $subject  = [string]$m.Subject } catch { }
                try { $sender   = [string]$m.SenderName } catch { }
                try { $received = $m.ReceivedTime } catch { }
                try { $isUnread = ([int]$m.UnRead -eq 1) } catch { }

                $mail += [pscustomobject]@{
                    account = $name
                    from    = $sender
                    subject = $subject
                    time    = $(if ($received) { $received.ToString('o') } else { '' })
                    unread  = $isUnread
                }
                $taken++
            }
        }
    }
    catch { }

    # ------------------------------------------------------------- calendar ---
    # 9 = olFolderCalendar. IncludeRecurrences turns a weekly meeting into the
    # actual occurrences inside the window instead of its original start date.
    try {
        $folder = $store.GetDefaultFolder(9)
        if ($folder) {
            $items = $folder.Items
            try { $items.IncludeRecurrences = $true } catch { }
            try { $items.Sort('[Start]') } catch { }
            try { $items = $items.Restrict("[Start] <= '" + $until.ToString('g') + "'") } catch { }
            $taken = 0
            for ($i = 1; $i -le $items.Count -and $taken -lt $CAL_LIMIT; $i++) {
                $a = $null
                try { $a = $items.Item($i) } catch { }
                if ($null -eq $a) { continue }

                $start = $null
                try { $start = $a.Start } catch { }
                if (-not $start) { continue }
                if ($start -lt $now.AddHours(-1)) { continue }
                if ($start -gt $until) { continue }

                $subject = ''
                $allday  = $false
                try { $subject = [string]$a.Subject } catch { }
                try { $allday  = ([int]$a.AllDayEvent -eq 1) } catch { }

                $cal += [pscustomobject]@{
                    account = $name
                    subject = $subject
                    time    = $start.ToString('o')
                    allday  = $allday
                }
                $taken++
            }
        }
    }
    catch { }
}

# Both lists are already newest/soonest first per store; merge by time.
$mail = @($mail | Sort-Object -Property { $_.time } -Descending)
$cal  = @($cal  | Sort-Object -Property { $_.time })

Emit ([pscustomobject]@{
    ok       = $true
    accounts = @($accounts)
    unread   = $unread
    mail     = @($mail)
    calendar = @($cal)
    at       = $now.ToString('o')
})

exit 0

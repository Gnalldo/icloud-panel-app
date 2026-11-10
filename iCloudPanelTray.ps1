# Tray icon + window management for the WebView2 iCloud panel.
# The panel binary is ours, so DWM's own rounded corners and drop shadow apply.
param([switch]$Show, [switch]$Lock, [switch]$Diagnose, [switch]$SelfTest, [switch]$NewNote)

$ErrorActionPreference = 'Stop'

$appDir     = $PSScriptRoot
$panelExe   = Join-Path $appDir 'panel\iCloudPanel.exe'
$iconPath   = Join-Path $appDir 'icloud.ico'
$logPath    = Join-Path $appDir 'panel-tray.log'
$configPath = Join-Path $appDir 'config.json'
$Margin     = 12

$script:PanelW = 594
$script:PanelH = 851
$script:PanelX = $null
$script:PanelY = $null
$script:Radius = 14
$script:TopMost = $false
$script:Pinned = $false
$script:AutoHide = $false
$script:BgKind = 'default'
$script:BgColor = '#0b2a52'
$script:BgImage = ''
$script:BgOpacity = 1.0
$script:BgOffset = 0
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content -Raw -Path $configPath | ConvertFrom-Json
        if ($cfg.width)  { $script:PanelW = [int]$cfg.width }
        if ($cfg.height) { $script:PanelH = [int]$cfg.height }
        if ($null -ne $cfg.x) { $script:PanelX = [int]$cfg.x }
        if ($null -ne $cfg.y) { $script:PanelY = [int]$cfg.y }
        if ($null -ne $cfg.radius) { $script:Radius = [int]$cfg.radius }
        if ($null -ne $cfg.topmost) { $script:TopMost = [bool]$cfg.topmost }
        if ($null -ne $cfg.pinned)  { $script:Pinned  = [bool]$cfg.pinned }
        if ($null -ne $cfg.autohide) { $script:AutoHide = [bool]$cfg.autohide }
        if ($cfg.bgkind) { $script:BgKind = [string]$cfg.bgkind }
        if ($cfg.bgcolor) { $script:BgColor = [string]$cfg.bgcolor }
        if ($null -ne $cfg.bgimage) { $script:BgImage = [string]$cfg.bgimage }
        if ($null -ne $cfg.bgopacity) { $script:BgOpacity = [double]$cfg.bgopacity }
        if ($null -ne $cfg.bgoffset) { $script:BgOffset = [int]$cfg.bgoffset }
    } catch { }
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PW
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    // Rounds our own window (regions work here, unlike Brave's DComp window).
    public static bool RoundRegion(IntPtr h, int radius)
    {
        RECT c;
        GetClientRect(h, out c);
        int w = c.R - c.L;
        int ht = c.B - c.T;
        if (w <= 0 || ht <= 0) return false;
        if (radius <= 0) { return SetWindowRgn(h, IntPtr.Zero, true) != 0; }
        int ell = Math.Max(2, Math.Min(2 * radius, Math.Min(w, ht)));
        IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, ht + 1, ell, ell);
        if (rgn == IntPtr.Zero) return false;
        return SetWindowRgn(h, rgn, true) != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT data, uint flags, uint timeout, out IntPtr result);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }
    public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    // Asks the running panel to create a note in the page it already has open.
    // The panel answers WM_COPYDATA itself, so a non-zero result means it took it.
    public static bool SendNewNote(IntPtr h)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes("new-note");
        IntPtr buf = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            COPYDATASTRUCT data = new COPYDATASTRUCT();
            data.dwData = new IntPtr(0x4E4F5445);   // 'NOTE'
            data.cbData = bytes.Length;
            data.lpData = buf;
            IntPtr result;
            IntPtr ok = SendMessageTimeout(h, 0x004A, IntPtr.Zero, ref data, 0x0002, 5000, out result);
            return ok != IntPtr.Zero && result != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static int SetDwm(IntPtr h, int attr, int val)
    {
        return DwmSetWindowAttribute(h, attr, ref val, 4);
    }

    public static bool IsPanelClass(IntPtr h)
    {
        System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
        GetClassName(h, cls, 256);
        return cls.ToString().StartsWith("WindowsForms10");
    }

    public static string ClassOf(IntPtr h)
    {
        System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
        GetClassName(h, cls, 256);
        return cls.ToString();
    }

    public static IntPtr Best(uint[] pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (Array.IndexOf(pids, pid) < 0) return true;
            if (GetWindowTextLength(h) == 0) return true;
            // No visibility test on purpose: with "hide when not focused" the panel
            // is a hidden window that still has to be found so the tray can show it
            // again. The console is excluded by the class test below.
            // The panel is a WinForms form. The PowerShell host also owns a console
            // window (the blue one); never treat that as the panel.
            if (!IsPanelClass(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // [x, y, width, height] of the client area in screen coordinates
    public static int[] ClientOnScreen(IntPtr h)
    {
        RECT c;
        GetClientRect(h, out c);
        POINT tl = new POINT(); tl.X = c.L; tl.Y = c.T;
        ClientToScreen(h, ref tl);
        return new int[] { tl.X, tl.Y, c.R - c.L, c.B - c.T };
    }
}
'@
[void][PW]::SetProcessDPIAware()

# This script is a tray app: its own PowerShell console must stay hidden, or it
# can sit on top of the panel as a blue box (and it is not the panel).
$trayConsole = [PW]::GetConsoleWindow()
if ($trayConsole -ne [IntPtr]::Zero) { [void][PW]::ShowWindow($trayConsole, 0) }

function Write-Log([string]$message) {
    try { Add-Content -Path $logPath -Value ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message) } catch { }
}

# Re-read config.json so a position/size saved by the panel is honoured on the
# next show, instead of re-using whatever was on disk when the tray started.
function Refresh-Config {
    if (-not (Test-Path $configPath)) { return }
    try {
        $cfg = Get-Content -Raw -Path $configPath | ConvertFrom-Json
        if ($cfg.width)  { $script:PanelW = [int]$cfg.width }
        if ($cfg.height) { $script:PanelH = [int]$cfg.height }
        if ($null -ne $cfg.x) { $script:PanelX = [int]$cfg.x }
        if ($null -ne $cfg.y) { $script:PanelY = [int]$cfg.y }
        if ($null -ne $cfg.radius) { $script:Radius = [int]$cfg.radius }
        if ($null -ne $cfg.topmost) { $script:TopMost = [bool]$cfg.topmost }
        if ($null -ne $cfg.pinned)  { $script:Pinned  = [bool]$cfg.pinned }
        if ($null -ne $cfg.autohide) { $script:AutoHide = [bool]$cfg.autohide }
        if ($cfg.bgkind) { $script:BgKind = [string]$cfg.bgkind }
        if ($cfg.bgcolor) { $script:BgColor = [string]$cfg.bgcolor }
        if ($null -ne $cfg.bgimage) { $script:BgImage = [string]$cfg.bgimage }
        if ($null -ne $cfg.bgopacity) { $script:BgOpacity = [double]$cfg.bgopacity }
        if ($null -ne $cfg.bgoffset) { $script:BgOffset = [int]$cfg.bgoffset }
    } catch { }
}

$script:Hwnd = [IntPtr]::Zero

function Get-PanelPids {
    # The panel runs either as the compiled exe or hosted by powershell.exe
    # (Smart App Control on this machine blocks fresh unsigned exes).
    $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -eq 'iCloudPanel.exe' -or
        ($_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*iCloudPanelHost.ps1*')
    })
    return @($procs | ForEach-Object { [uint32]$_.ProcessId })
}

function Find-PanelWindow {
    if ($script:Hwnd -ne [IntPtr]::Zero -and [PW]::IsWindow($script:Hwnd) -and [PW]::IsPanelClass($script:Hwnd)) {
        return $script:Hwnd
    }
    $pids = Get-PanelPids
    if ($pids.Count -eq 0) { $script:Hwnd = [IntPtr]::Zero; return $script:Hwnd }
    $script:Hwnd = [PW]::Best([uint32[]]$pids)
    if ($script:Hwnd -ne [IntPtr]::Zero) { Write-Log ('panel window: ' + [PW]::ClassOf($script:Hwnd)) }
    return $script:Hwnd
}

function Start-Panel {
    $hostLauncher = Join-Path $appDir 'start-panel-host.vbs'
    if (Test-Path $hostLauncher) {
        Write-Log 'launching panel (powershell host)'
        Start-Process -FilePath (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList $hostLauncher
        return
    }
    if (-not (Test-Path $panelExe)) { throw "Panel binary missing: $panelExe" }
    Write-Log 'launching panel (exe)'
    Start-Process -FilePath $panelExe -WorkingDirectory (Split-Path $panelExe)
}

function Wait-PanelWindow([int]$timeoutMs = 20000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $h = Find-PanelWindow
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 300
    }
    return [IntPtr]::Zero
}

function Set-PanelPlacement([IntPtr]$h, [switch]$AtCursor) {
    $w  = $script:PanelW
    $ht = $script:PanelH

    if ($null -ne $script:PanelX -and $null -ne $script:PanelY) {
        # Keep the saved spot on whichever monitor holds most of the panel.
        $x = [int]$script:PanelX
        $y = [int]$script:PanelY
        $centre = New-Object System.Drawing.Point(($x + [int]($w / 2)), ($y + [int]($ht / 2)))
        $savedScreen = [System.Windows.Forms.Screen]::FromPoint($centre)
        $area = $savedScreen.WorkingArea

        if ($AtCursor) {
            # "New iCloud Note" follows the mouse: same size, and the same edges it
            # hugs on its own monitor, on whichever display the pointer is on.
            $target = [System.Windows.Forms.Screen]::FromPoint([System.Windows.Forms.Cursor]::Position)
            if ($target.DeviceName -ne $savedScreen.DeviceName) {
                $ta = $target.WorkingArea
                $leftGap   = $x - $area.Left
                $rightGap  = $area.Right - ($x + $w)
                $topGap    = $y - $area.Top
                $bottomGap = $area.Bottom - ($y + $ht)
                if ($leftGap -le $rightGap) { $x = $ta.Left + $leftGap } else { $x = $ta.Right - $w - $rightGap }
                if ($topGap -le $bottomGap) { $y = $ta.Top + $topGap } else { $y = $ta.Bottom - $ht - $bottomGap }
                $area = $ta
                Write-Log ('cursor monitor: ' + $target.DeviceName)
            }
        }

        # A display smaller than the saved size means the panel shrinks to fit
        # instead of hanging off the edge; its own show keeps the saved size.
        if ($w -gt ($area.Width - 2 * $Margin))   { $w  = [Math]::Max(320, $area.Width  - 2 * $Margin) }
        if ($ht -gt ($area.Height - 2 * $Margin)) { $ht = [Math]::Max(320, $area.Height - 2 * $Margin) }

        if ($x -lt $area.Left) { $x = $area.Left }
        if ($y -lt $area.Top)  { $y = $area.Top }
        if ($x + $w -gt $area.Right)  { $x = $area.Right  - $w }
        if ($y + $ht -gt $area.Bottom) { $y = $area.Bottom - $ht }
    } else {
        $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        $w  = [Math]::Min($w,  [Math]::Max(320, $wa.Width  - 40))
        $ht = [Math]::Min($ht, [Math]::Max(320, $wa.Height - 40))
        $x = $wa.Right  - $w  - $Margin
        $y = $wa.Bottom - $ht - $Margin
    }

    [void][PW]::MoveWindow($h, $x, $y, $w, $ht, $true)
    Start-Sleep -Milliseconds 80
    Write-Log "placed: ${w}x${ht} at $x,$y"
}

function Set-PanelLook([IntPtr]$h) {
    # DWM does the rounding and the shadow; no border colour at all.
    $hrRound  = [PW]::SetDwm($h, 33, 2)     # DWMWCP_ROUND
    $hrBorder = [PW]::SetDwm($h, 34, -2)    # DWMWA_COLOR_NONE
    Write-Log "look: round=$hrRound border=$hrBorder"
}

function Hide-Panel([IntPtr]$h) {
    [void][PW]::ShowWindow($h, 0)
    Write-Log 'hidden'
}

function Show-Panel([switch]$AtCursor) {
    Refresh-Config
    $h = Find-PanelWindow
    if ($h -eq [IntPtr]::Zero) {
        Start-Panel
        $h = Wait-PanelWindow
        if ($h -eq [IntPtr]::Zero) {
            # A host can survive with no window while still holding the
            # single-instance mutex; every launch then exits as "another host is
            # already running", so the icon looks dead for good. Clear it and
            # try once more before giving up.
            Write-Log 'stale host (no window) - restarting it'
            foreach ($procId in (Get-PanelPids)) {
                try { Stop-Process -Id $procId -Force -ErrorAction Stop } catch { }
            }
            $script:Hwnd = [IntPtr]::Zero
            Start-Sleep -Milliseconds 800
            Start-Panel
            $h = Wait-PanelWindow
        }
        if ($h -eq [IntPtr]::Zero) { Write-Log 'ERROR: panel window never appeared'; return }
    }
    if (-not $script:Unlocked) {
        Strip-Frame $h
        Set-PanelPlacement $h -AtCursor:$AtCursor
        Set-PanelLook $h
        [void][PW]::RoundRegion($h, [int]$script:Radius)
        Start-Sleep -Milliseconds 200
        [void][PW]::RoundRegion($h, [int]$script:Radius)
    }
    # Always raise on show, pinned or not: a pinned panel comes to the front for
    # the click and drops back to its layer when focus leaves (panel handles that).
    [void][PW]::ShowWindow($h, 5)
    [void][PW]::BringWindowToTop($h)
    [void][PW]::SetForegroundWindow($h)
    if ($script:Pinned) { Write-Log 'shown (pinned: raised until focus leaves)' } else { Write-Log 'shown' }
}

# "New iCloud Note" from the Start menu: show the panel on the monitor under
# the mouse, then ask it to open a new note in the page it already has loaded.
function Invoke-NewNote {
    Show-Panel -AtCursor
    $h = Find-PanelWindow
    if ($h -eq [IntPtr]::Zero) { Write-Log 'new-note: no panel window'; return }
    # A panel that is still booting WebView2 queues the command and replays it
    # once iCloud has loaded, so one accepted send is enough. Retry anyway in
    # case the handle was found while the window was still being created.
    for ($try = 0; $try -lt 5; $try++) {
        if ([PW]::SendNewNote($h)) { Write-Log 'new-note: sent to panel'; return }
        Start-Sleep -Milliseconds 400
        $h = Find-PanelWindow
        if ($h -eq [IntPtr]::Zero) { break }
    }
    Write-Log 'new-note: panel did not accept the command'
}

function Toggle-Panel {
    $h = Find-PanelWindow
    if ($h -ne [IntPtr]::Zero -and [PW]::IsWindowVisible($h)) { Hide-Panel $h } else { Show-Panel }
}

function Strip-Frame([IntPtr]$h) {
    $GWL_STYLE     = -16
    $WS_CAPTION    = 0x00C00000
    $WS_THICKFRAME = 0x00040000
    $style = [PW]::GetWindowLong($h, $GWL_STYLE)
    $new = $style -band (-bnot ($WS_CAPTION -bor $WS_THICKFRAME))
    [void][PW]::SetWindowLong($h, $GWL_STYLE, $new)
    [void][PW]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0020 -bor 0x0002 -bor 0x0001 -bor 0x0004 -bor 0x0010)
    Write-Log 'frame stripped'
}

function Restore-Frame([IntPtr]$h) {
    $GWL_STYLE      = -16
    $WS_CAPTION     = 0x00C00000
    $WS_THICKFRAME  = 0x00040000
    $WS_SYSMENU     = 0x00080000
    $WS_MINIMIZEBOX = 0x00020000
    $WS_MAXIMIZEBOX = 0x00010000
    $style = [PW]::GetWindowLong($h, $GWL_STYLE)
    $new = $style -bor $WS_CAPTION -bor $WS_THICKFRAME -bor $WS_SYSMENU -bor $WS_MINIMIZEBOX -bor $WS_MAXIMIZEBOX
    [void][PW]::SetWindowLong($h, $GWL_STYLE, $new)
    [void][PW]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0020 -bor 0x0002 -bor 0x0001 -bor 0x0004 -bor 0x0010)
    Write-Log 'frame restored for resize'
}

function Unlock-Panel {
    $h = Find-PanelWindow
    if ($h -eq [IntPtr]::Zero) { Start-Panel; $h = Wait-PanelWindow }
    if ($h -eq [IntPtr]::Zero) { return }
    $script:Unlocked = $true
    [void][PW]::ShowWindow($h, 5)
    [void][PW]::RoundRegion($h, 0)
    Restore-Frame $h
    Write-Log 'unlocked'
}

function Lock-Panel {
    $h = Find-PanelWindow
    if ($h -eq [IntPtr]::Zero) { Start-Panel; $h = Wait-PanelWindow }
    if ($h -eq [IntPtr]::Zero) { return }
    $c = [PW]::ClientOnScreen($h)
    if ($c[2] -gt 0 -and $c[3] -gt 0) {
        $script:PanelW = $c[2]
        $script:PanelH = $c[3]
        $script:PanelX = $c[0]
        $script:PanelY = $c[1]
        @{ width = $c[2]; height = $c[3]; x = $c[0]; y = $c[1]; radius = $script:Radius; topmost = $script:TopMost; pinned = $script:Pinned; autohide = $script:AutoHide; bgkind = $script:BgKind; bgcolor = $script:BgColor; bgimage = $script:BgImage; bgopacity = $script:BgOpacity; bgoffset = $script:BgOffset } |
            ConvertTo-Json -Compress | Set-Content -Path $configPath -Encoding ASCII
        Write-Log "saved $($c[2])x$($c[3]) at $($c[0]),$($c[1])"
    }
    $script:Unlocked = $false
    Strip-Frame $h
    Set-PanelPlacement $h
    Set-PanelLook $h
    [void][PW]::RoundRegion($h, [int]$script:Radius)
    Start-Sleep -Milliseconds 200
    [void][PW]::RoundRegion($h, [int]$script:Radius)
    Write-Log "locked in place (radius $($script:Radius))"
}

function Restart-Panel {
    foreach ($procId in (Get-PanelPids)) {
        try { Stop-Process -Id $procId -Force -ErrorAction Stop } catch { }
    }
    $script:Hwnd = [IntPtr]::Zero
    Start-Sleep -Milliseconds 800
    Show-Panel
}

function Write-Diagnostics([IntPtr]$h) {
    $wr = New-Object PW+RECT
    [void][PW]::GetWindowRect($h, [ref]$wr)
    $c = [PW]::ClientOnScreen($h)
    Write-Output "window: $($wr.R - $wr.L)x$($wr.B - $wr.T) at $($wr.L),$($wr.T)"
    Write-Output "client: $($c[2])x$($c[3]) at $($c[0]),$($c[1])"
    Write-Output ('style: 0x{0:X8}' -f [PW]::GetWindowLong($h, -16))
    Write-Output "visible: $([PW]::IsWindowVisible($h))"
}

# ------------------------------------------------------------- entry points ---
if ($Diagnose) {
    $h = Find-PanelWindow
    if ($h -eq [IntPtr]::Zero) { Start-Panel; $h = Wait-PanelWindow }
    if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: no panel window'; exit 1 }
    Write-Diagnostics $h
    exit 0
}

if ($Show)    { Show-Panel;  exit 0 }
if ($Lock)    { Lock-Panel;  exit 0 }
if ($NewNote) { Invoke-NewNote; exit 0 }

if ($SelfTest) {
    $h = Wait-PanelWindow 3000
    if ($h -eq [IntPtr]::Zero) { Start-Panel; $h = Wait-PanelWindow }
    if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: no panel window'; exit 1 }
    Show-Panel
    Start-Sleep -Milliseconds 1200
    Write-Diagnostics $h
    Hide-Panel $h
    Start-Sleep -Milliseconds 600
    Write-Output "visible after hide: $([PW]::IsWindowVisible($h))"
    Show-Panel
    Start-Sleep -Milliseconds 600
    Write-Output "visible after show: $([PW]::IsWindowVisible($h))"
    exit 0
}

# ---------------------------------------------------------------- tray app ---
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\iCloudPanelTray', [ref]$createdNew)
if (-not $createdNew) { Write-Log 'tray already running'; exit 0 }

$icon = New-Object System.Drawing.Icon($iconPath)
$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = $icon
$notify.Text = 'iCloud'
$notify.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$itemRestart = $menu.Items.Add('Reload panel')
$itemRestart.add_Click({ try { Restart-Panel } catch { Write-Log "restart: $_" } })
$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)) | Out-Null
$itemExit = $menu.Items.Add('Exit tray icon')
$itemExit.add_Click({
    $notify.Visible = $false
    $notify.Dispose()
    # Exit means the whole thing: the panel used to outlive the icon, leaving a
    # window with no way to reach it.
    Write-Log 'tray exiting, stopping panel'
    foreach ($p in Get-PanelPids) {
        try { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue } catch { }
    }
    [System.Windows.Forms.Application]::ExitThread()
})
# Stock WinForms menus are always light, so repaint it to match the Windows
# theme the rest of the panel (and WebView2) already follows.
$themeFile = Join-Path $appDir 'tray-menu-theme.ps1'
if (Test-Path $themeFile) {
    . $themeFile
    [void](Set-DarkTrayMenu $menu)
}

$notify.ContextMenuStrip = $menu
$notify.add_MouseClick({
    param($sender, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        try { Toggle-Panel } catch { Write-Log "click: $_" }
    }
})

Write-Log 'panel tray started'
[System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))

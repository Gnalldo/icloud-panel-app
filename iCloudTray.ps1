# iCloud tray icon: a notification-area button that shows/hides the iCloud app
# window (Brave app-mode window on its own profile) as a frameless docked panel.
param([switch]$SelfTest, [switch]$Show, [switch]$Diagnose, [switch]$Lock)

$ErrorActionPreference = 'Stop'

$appDir     = $PSScriptRoot
$profileDir = Join-Path $appDir 'profile'
$iconPath   = Join-Path $appDir 'icloud.ico'
$logPath    = Join-Path $appDir 'tray.log'
$configPath = Join-Path $appDir 'config.json'

$Margin      = 12
$script:PanelW = 1100
$script:PanelH = 850
$script:Radius = 14          # rounded-corner radius in pixels (macOS-style card)
$script:PanelX = $null       # saved page position; $null = default bottom-right
$script:PanelY = $null
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content -Raw -Path $configPath | ConvertFrom-Json
        if ($cfg.width)  { $script:PanelW = [int]$cfg.width }
        if ($cfg.height) { $script:PanelH = [int]$cfg.height }
        if ($null -ne $cfg.radius) { $script:Radius = [int]$cfg.radius }
        if ($null -ne $cfg.x) { $script:PanelX = [int]$cfg.x }
        if ($null -ne $cfg.y) { $script:PanelY = [int]$cfg.y }
    } catch { }
}

$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'BraveSoftware\Brave-Browser\Application\brave.exe')
    'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe'
    'C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe'
    'C:\Program Files\Google\Chrome\Application\chrome.exe'
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)
$brave = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ICW
{
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    public static int SetDwm(IntPtr h, int attr, int val)
    {
        return DwmSetWindowAttribute(h, attr, ref val, 4);
    }

    // RGN_* result of GetWindowRgn: 0 = error/no region, 1 = null, 2 = simple rect, 3 = complex (rounded)
    public static int RegionType(IntPtr h)
    {
        IntPtr probe = CreateRectRgn(0, 0, 0, 0);
        int t = GetWindowRgn(h, probe);
        DeleteObject(probe);
        return t;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }

    public static IntPtr Best(uint[] pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (Array.IndexOf(pids, pid) < 0) return true;
            if (GetWindow(h, 4) != IntPtr.Zero) return true;
            if (GetWindowTextLength(h) == 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // Largest descendant whose class name contains the needle (the web contents).
    public static IntPtr PageChild(IntPtr parent)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            string cls = sb.ToString();
            if (cls.IndexOf("RenderWidgetHost", StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // Brave reserves height for a title bar it no longer draws. Stretching the
    // page child to the whole client area removes that dead strip.
    public static bool StretchPageToClient(IntPtr win)
    {
        IntPtr page = PageChild(win);
        if (page == IntPtr.Zero) return false;
        RECT c;
        if (!GetClientRect(win, out c)) return false;
        SetWindowPos(page, IntPtr.Zero, 0, 0, c.R - c.L, c.B - c.T, 0x0004 | 0x0010);
        return true;
    }

    // [window width, window height, page left, page top, page width, page height]
    // page values are relative to the window origin.
    public static int[] PageRects(IntPtr win, IntPtr page)
    {
        RECT w;
        GetWindowRect(win, out w);
        int winW = w.R - w.L, winH = w.B - w.T;

        if (page == IntPtr.Zero || !IsWindow(page))
        {
            RECT c;
            GetClientRect(win, out c);
            int cw = c.R - c.L, ch = c.B - c.T;
            if (cw <= 0 || ch <= 0) return new int[] { winW, winH, 0, 0, winW, winH };
            return new int[] { winW, winH, 0, 0, cw, ch };
        }

        RECT p;
        GetWindowRect(page, out p);
        return new int[] { winW, winH, p.L - w.L, p.T - w.T, p.R - p.L, p.B - p.T };
    }

    // [x, y, width, height] of the page area in screen coordinates
    public static int[] PageScreenRect(IntPtr win, IntPtr page)
    {
        if (page == IntPtr.Zero || !IsWindow(page))
        {
            RECT w2, c2;
            GetWindowRect(win, out w2);
            GetClientRect(win, out c2);
            return new int[] { w2.L, w2.T, c2.R - c2.L, c2.B - c2.T };
        }
        RECT p;
        GetWindowRect(page, out p);
        return new int[] { p.L, p.T, p.R - p.L, p.B - p.T };
    }

    // "class @x,y wxh" for every descendant, for diagnostics.
    public static string[] Children(IntPtr parent)
    {
        List<string> list = new List<string>();
        RECT pr;
        GetWindowRect(parent, out pr);
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            RECT r;
            if (GetWindowRect(h, out r))
            {
                list.Add(string.Format("{0} @{1},{2} {3}x{4} visible={5}",
                    sb.ToString(), r.L - pr.L, r.T - pr.T, r.R - r.L, r.B - r.T, IsWindowVisible(h)));
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
}
'@

function Write-Log([string]$message) {
    try {
        Add-Content -Path $logPath -Value ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
    } catch { }
}

$script:Hwnd = [IntPtr]::Zero
$script:Unlocked = $false
$script:Chromeless = [IntPtr]::Zero

function Get-AppPids {
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='brave.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profileDir*" })
    return @($procs | ForEach-Object { [uint32]$_.ProcessId })
}

function Find-AppWindow {
    if ($script:Hwnd -ne [IntPtr]::Zero -and [ICW]::IsWindow($script:Hwnd)) { return $script:Hwnd }
    $pids = Get-AppPids
    if ($pids.Count -eq 0) { $script:Hwnd = [IntPtr]::Zero; return $script:Hwnd }
    $script:Hwnd = [ICW]::Best([uint32[]]$pids)
    return $script:Hwnd
}

function Get-PageHandle([IntPtr]$h) {
    $page = [ICW]::PageChild($h)
    if ($page -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    return $page
}

function Start-App {
    if (-not $brave) { throw 'No Brave/Chromium browser found.' }
    Write-Log 'launching iCloud app window'
    Start-Process -FilePath $brave -ArgumentList @(
        '--app=https://www.icloud.com/'
        "--user-data-dir=`"$profileDir`""
        '--no-first-run'
        '--no-default-browser-check'
        '--hide-scrollbars'
        "--window-size=$($script:PanelW),$($script:PanelH)"
    )
}

function Wait-AppWindow([int]$timeoutMs = 20000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $h = Find-AppWindow
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 300
    }
    return [IntPtr]::Zero
}

function Remove-WindowChrome([IntPtr]$h, [switch]$Force) {
    if ($h -eq [IntPtr]::Zero) { return }
    if (-not $Force -and $script:Chromeless -eq $h) { return }

    $GWL_STYLE     = -16
    $WS_CAPTION    = 0x00C00000
    $WS_THICKFRAME = 0x00040000
    $WS_BORDER     = 0x00800000
    $WS_DLGFRAME   = 0x00400000
    $WS_POPUP      = 0x80000000

    $style = [ICW]::GetWindowLong($h, $GWL_STYLE)
    $strip = $WS_CAPTION -bor $WS_THICKFRAME -bor $WS_BORDER -bor $WS_DLGFRAME
    $new   = ($style -band (-bnot $strip)) -bor $WS_POPUP
    [void][ICW]::SetWindowLong($h, $GWL_STYLE, $new)
    # SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    [void][ICW]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0020 -bor 0x0002 -bor 0x0001 -bor 0x0004 -bor 0x0010)
    $script:Chromeless = $h
    Write-Log ('style 0x{0:X8} -> 0x{1:X8}' -f $style, $new)
}

function Restore-WindowFrame([IntPtr]$h) {
    $GWL_STYLE     = -16
    $WS_CAPTION    = 0x00C00000
    $WS_THICKFRAME = 0x00040000
    $WS_SYSMENU    = 0x00080000
    $WS_MINIMIZEBOX = 0x00020000
    $WS_MAXIMIZEBOX = 0x00010000

    [void][ICW]::SetWindowRgn($h, [IntPtr]::Zero, $true)   # clear clipping
    $style = [ICW]::GetWindowLong($h, $GWL_STYLE)
    $new = $style -bor $WS_CAPTION -bor $WS_THICKFRAME -bor $WS_SYSMENU -bor $WS_MINIMIZEBOX -bor $WS_MAXIMIZEBOX
    [void][ICW]::SetWindowLong($h, $GWL_STYLE, $new)
    [void][ICW]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0020 -bor 0x0002 -bor 0x0001 -bor 0x0004 -bor 0x0010)
    $script:Chromeless = [IntPtr]::Zero
    Write-Log 'frame restored for resizing'
}

function Clip-ToPage([IntPtr]$h) {
    if ($h -eq [IntPtr]::Zero) { return }
    $page = Get-PageHandle $h
    $r = [ICW]::PageRects($h, $page)
    $pl = $r[2]; $pt = $r[3]; $pw = $r[4]; $ph = $r[5]
    if ($pw -le 0 -or $ph -le 0) { return }

    # DWMWA_NCRENDERING_POLICY(2) = DWMNCRP_DISABLED: no DWM border, no shadow.
    # DWMWA_WINDOW_CORNER_PREFERENCE(33) = DWMWCP_DONOTROUND: we round ourselves.
    # DWMWA_BORDER_COLOR(34) = DWMWA_COLOR_NONE: explicit "no border colour".
    $hrNc  = [ICW]::SetDwm($h, 2, 2)
    $hrCrn = [ICW]::SetDwm($h, 33, 1)
    $hrBr  = [ICW]::SetDwm($h, 34, -2)

    $radius = [Math]::Max(0, [int]$script:Radius)
    $ell = [Math]::Min(2 * $radius, [Math]::Min($pw, $ph))
    if ($ell -gt 0) {
        $rgn = [ICW]::CreateRoundRectRgn($pl, $pt, $pl + $pw + 1, $pt + $ph + 1, $ell, $ell)
    } else {
        $rgn = [ICW]::CreateRectRgn($pl, $pt, $pl + $pw, $pt + $ph)
    }
    if ($rgn -eq [IntPtr]::Zero) { return }
    # Windows owns the region after SetWindowRgn; do not delete it here.
    [void][ICW]::SetWindowRgn($h, $rgn, $true)
    Write-Log ("clipped to page {0}x{1} at {2},{3} radius {4} (dwm nc={5} corner={6} border={7})" -f $pw, $ph, $pl, $pt, $radius, $hrNc, $hrCrn, $hrBr)
}

function Set-PanelPlacement([IntPtr]$h) {
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

    # 1. Size the window so the PAGE matches the saved size.
    $r  = [ICW]::PageRects($h, (Get-PageHandle $h))
    $dw = $r[0] - $r[4]
    $dh = $r[1] - $r[5]
    $targetW = [Math]::Min($script:PanelW, [Math]::Max(480, $wa.Width  - 40))
    $targetH = [Math]::Min($script:PanelH, [Math]::Max(480, $wa.Height - 40))
    [void][ICW]::MoveWindow($h, $wa.Left, $wa.Top, ($targetW + $dw), ($targetH + $dh), $true)
    Start-Sleep -Milliseconds 120

    # 2. Put the page at the saved spot, or bottom-right when none is saved.
    $r  = [ICW]::PageRects($h, (Get-PageHandle $h))
    $pw = $r[4]; $ph = $r[5]; $pl = $r[2]; $pt = $r[3]
    if ($null -ne $script:PanelX -and $null -ne $script:PanelY) {
        $pageX = [int]$script:PanelX
        $pageY = [int]$script:PanelY
    } else {
        $pageX = $wa.Right  - $pw - $Margin
        $pageY = $wa.Bottom - $ph - $Margin
    }

    # Keep it on a real monitor (resolution changes, unplugged display).
    $screen = [System.Windows.Forms.Screen]::FromPoint((New-Object System.Drawing.Point($pageX, $pageY)))
    $area = $screen.WorkingArea
    if ($pageX -lt $area.Left) { $pageX = $area.Left }
    if ($pageY -lt $area.Top)  { $pageY = $area.Top }
    if ($pageX + $pw -gt $area.Right)  { $pageX = $area.Right  - $pw }
    if ($pageY + $ph -gt $area.Bottom) { $pageY = $area.Bottom - $ph }

    [void][ICW]::MoveWindow($h, ($pageX - $pl), ($pageY - $pt), $r[0], $r[1], $true)
    Start-Sleep -Milliseconds 60
    Write-Log "placed: page ${pw}x${ph} at $pageX,$pageY"
}

function Show-App {
    $h = Find-AppWindow
    if ($h -eq [IntPtr]::Zero) {
        Start-App
        $h = Wait-AppWindow
        if ($h -eq [IntPtr]::Zero) { Write-Log 'ERROR: window never appeared'; return }
    }
    if (-not $script:Unlocked) {
        Remove-WindowChrome $h
        Set-PanelPlacement $h
        Clip-ToPage $h
    }
    [void][ICW]::ShowWindow($h, 5)          # SW_SHOW
    [void][ICW]::SetForegroundWindow($h)
    if (-not $script:Unlocked) {
        # Chromium settles its layout shortly after being shown; re-apply.
        Start-Sleep -Milliseconds 350
        Remove-WindowChrome $h -Force
        Set-PanelPlacement $h
        Clip-ToPage $h
    }
    Write-Log 'shown'
}

function Hide-App {
    $h = Find-AppWindow
    if ($h -eq [IntPtr]::Zero) { return }
    [void][ICW]::ShowWindow($h, 0)          # SW_HIDE
    Write-Log 'hidden'
}

function Toggle-App {
    $h = Find-AppWindow
    if ($h -ne [IntPtr]::Zero -and [ICW]::IsWindowVisible($h)) { Hide-App } else { Show-App }
}

function Restart-App {
    foreach ($procId in (Get-AppPids)) {
        try { Stop-Process -Id $procId -Force -ErrorAction Stop } catch { }
    }
    $script:Hwnd = [IntPtr]::Zero
    $script:Chromeless = [IntPtr]::Zero
    Start-Sleep -Milliseconds 1200
    Show-App
}

function Unlock-Panel {
    $h = Find-AppWindow
    if ($h -eq [IntPtr]::Zero) { Show-App; $h = Find-AppWindow }
    if ($h -eq [IntPtr]::Zero) { return }
    $script:Unlocked = $true
    [void][ICW]::ShowWindow($h, 5)
    Restore-WindowFrame $h
    Write-Log 'unlocked for manual resize'
}

function Lock-Panel {
    $h = Find-AppWindow
    if ($h -eq [IntPtr]::Zero) { Show-App; $h = Find-AppWindow }
    if ($h -eq [IntPtr]::Zero) { return }
    $ps = [ICW]::PageScreenRect($h, (Get-PageHandle $h))
    if ($ps[2] -gt 0 -and $ps[3] -gt 0) {
        $script:PanelW = $ps[2]
        $script:PanelH = $ps[3]
        $script:PanelX = $ps[0]
        $script:PanelY = $ps[1]
        @{ width = $ps[2]; height = $ps[3]; x = $ps[0]; y = $ps[1]; radius = $script:Radius } |
            ConvertTo-Json -Compress | Set-Content -Path $configPath -Encoding ASCII
        Write-Log "saved size $($ps[2])x$($ps[3]) at $($ps[0]),$($ps[1])"
    }
    $script:Unlocked = $false
    $script:Chromeless = [IntPtr]::Zero
    Remove-WindowChrome $h -Force
    Set-PanelPlacement $h
    Clip-ToPage $h
    Write-Log 'locked in place'
}

function Get-Diagnostics([IntPtr]$h) {
    $r = [ICW]::PageRects($h, (Get-PageHandle $h))
    $ps = [ICW]::PageScreenRect($h, (Get-PageHandle $h))
    Write-Output "window: $($r[0])x$($r[1])  page: $($r[4])x$($r[5]) at $($r[2]),$($r[3])  delta: $($r[0]-$r[4])x$($r[1]-$r[5])"
    Write-Output "page on screen: $($ps[2])x$($ps[3]) at $($ps[0]),$($ps[1])"
    foreach ($line in [ICW]::Children($h)) { Write-Output "  child $line" }
}

# ------------------------------------------------------------------ non-tray ---
if ($Diagnose) {
    $h = Find-AppWindow
    if ($h -eq [IntPtr]::Zero) { Start-App; $h = Wait-AppWindow }
    if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: no app window'; exit 1 }
    [void][ICW]::ShowWindow($h, 5)
    Start-Sleep -Milliseconds 1500
    Write-Output "window handle: $h"
    Remove-WindowChrome $h -Force
    Start-Sleep -Milliseconds 300
    Get-Diagnostics $h
    exit 0
}

if ($Show) {
    Show-App
    exit 0
}

if ($Lock) {
    Lock-Panel
    exit 0
}

if ($SelfTest) {
    Write-Output "browser: $brave  panel: $($script:PanelW)x$($script:PanelH)"
    $h = Wait-AppWindow 2000
    if ($h -eq [IntPtr]::Zero) { Start-App; $h = Wait-AppWindow }
    if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: no app window'; exit 1 }
    Show-App
    Start-Sleep -Milliseconds 1200
    Write-Output "visible after show: $([ICW]::IsWindowVisible($h))"
    Get-Diagnostics $h
    Write-Output ('style now: 0x{0:X8}' -f [ICW]::GetWindowLong($h, -16))
    Write-Output "window region: $([ICW]::RegionType($h))  (0=error 1=null 2=rect 3=complex/rounded)  radius: $($script:Radius)"
    Hide-App
    Start-Sleep -Milliseconds 700
    Write-Output "visible after hide: $([ICW]::IsWindowVisible($h))"
    Show-App
    Start-Sleep -Milliseconds 700
    Write-Output "visible after second show: $([ICW]::IsWindowVisible($h))"
    exit 0
}

# ---------------------------------------------------------------- tray app ---
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\iCloudTrayIcon', [ref]$createdNew)
if (-not $createdNew) { Write-Log 'tray already running; exiting'; exit 0 }

$icon = New-Object System.Drawing.Icon($iconPath)

$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = $icon
$notify.Text = 'iCloud'
$notify.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$itemToggle = $menu.Items.Add('Show / hide iCloud')
$itemToggle.add_Click({ try { Toggle-App } catch { Write-Log "toggle error: $_" } })
$itemShow = $menu.Items.Add('Show iCloud')
$itemShow.add_Click({ try { Show-App } catch { Write-Log "show error: $_" } })
$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)) | Out-Null
$itemUnlock = $menu.Items.Add('Resize panel (unlock frame)')
$itemUnlock.add_Click({
    try {
        Unlock-Panel
        [void]$notify.ShowBalloonTip(6000, 'iCloud', 'Move or resize the panel where you want it, then right-click this icon and choose "Lock in place".', [System.Windows.Forms.ToolTipIcon]::Info)
    } catch { Write-Log "unlock error: $_" }
})
$itemLock = $menu.Items.Add('Lock in place')
$itemLock.add_Click({ try { Lock-Panel } catch { Write-Log "lock error: $_" } })
$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)) | Out-Null
$itemRestart = $menu.Items.Add('Restart iCloud')
$itemRestart.add_Click({ try { Restart-App } catch { Write-Log "restart error: $_" } })
$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)) | Out-Null
$itemExit = $menu.Items.Add('Exit tray icon')
$itemExit.add_Click({
    $notify.Visible = $false
    $notify.Dispose()
    Write-Log 'tray exiting'
    [System.Windows.Forms.Application]::ExitThread()
})
$notify.ContextMenuStrip = $menu

$notify.add_MouseClick({
    param($sender, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        try { Toggle-App } catch { Write-Log "click error: $_" }
    }
})

Write-Log 'tray icon started'
[System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))

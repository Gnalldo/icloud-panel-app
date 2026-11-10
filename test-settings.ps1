# Verifies the corner settings menu with real mouse input. Button and row
# positions come from the panel's own probe log, so the clicks are exact.
param([switch]$NoClick, [switch]$Bg, [string]$Out = 'shot-settings.png')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MouseTest4
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }
    public struct POINT { public int X, Y; }

    public static IntPtr Best(uint[] pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (Array.IndexOf(pids, p) < 0) return true;
            if (GetWindowTextLength(h) == 0) return true;
            // WinForms form only - the PowerShell host's console is not the panel.
            System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            if (!cls.ToString().StartsWith("WindowsForms10")) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static int[] ClientOnScreen(IntPtr h)
    {
        RECT c;
        GetClientRect(h, out c);
        POINT tl = new POINT(); tl.X = c.L; tl.Y = c.T;
        ClientToScreen(h, ref tl);
        return new int[] { tl.X, tl.Y, c.R - c.L, c.B - c.T };
    }

    public static bool IsTopMost(IntPtr h) { return (GetWindowLong(h, -20) & 0x00000008) != 0; }

    public static void Click()
    {
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(90);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }

    public static void Move(int x, int y)
    {
        SetCursorPos(x - 5, y - 5);
        System.Threading.Thread.Sleep(140);
        SetCursorPos(x, y);
    }
}
'@

$pids = @(Get-Process -Name 'iCloudPanel' -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
if ($pids.Count -eq 0) {
    $pids = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*iCloudPanelHost.ps1*'
    } | ForEach-Object { [uint32]$_.ProcessId })
}
if ($pids.Count -eq 0) { Write-Output 'FAIL: the panel is not running'; exit 1 }
$h = [MouseTest4]::Best([uint32[]]$pids)
if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: no panel window'; exit 1 }

$c = [MouseTest4]::ClientOnScreen($h)
Write-Host ("page: {0}x{1} at {2},{3}  topmost={4}" -f $c[2], $c[3], $c[0], $c[1], [MouseTest4]::IsTopMost($h))

# Probe rects are CSS pixels; the panel may be zoomed (config.json "zoom"), so
# they have to be scaled before they become screen points.
$zoom = 1.0
try {
    $cfg = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'config.json') | ConvertFrom-Json
    if ($cfg.zoom) { $zoom = [double]$cfg.zoom }
} catch { }
Write-Host ("zoom: {0}" -f $zoom)

$logPath = Join-Path $PSScriptRoot 'panel\panel.log'
$probe = Get-Content $logPath -Tail 80 | Where-Object { $_ -like '*probe:*' } | Select-Object -Last 1
if (-not $probe) { Write-Output 'FAIL: no probe line in panel.log'; exit 1 }
Write-Host "probe: $probe"

# Rect-Centre returns page-space centre of the group's left, top, width, height.
function Rect-Centre($match, [int]$groupBase) {
    $left = [int]$match.Groups[$groupBase].Value
    $top  = [int]$match.Groups[$groupBase + 1].Value
    $w    = [int]$match.Groups[$groupBase + 2].Value
    $hgt  = [int]$match.Groups[$groupBase + 3].Value
    $cx = $left + [int]($w / 2)
    $cy = $top + [int]($hgt / 2)
    return @($cx, $cy)
}

$gearMatch = [regex]::Match($probe, 'gearRect=(-?\d+),(-?\d+),(\d+)x(\d+)')
if (-not $gearMatch.Success) { Write-Output 'FAIL: probe has no gear rect'; exit 1 }
$gear = Rect-Centre $gearMatch 1

$rowMatch = [regex]::Match($probe, 'rows=(-?\d+),(-?\d+),(\d+)x(\d+)\|(-?\d+),(-?\d+),(\d+)x(\d+)')
if (-not $rowMatch.Success) { Write-Output 'FAIL: probe has no row rects'; exit 1 }
$row1 = Rect-Centre $rowMatch 1
$row2 = Rect-Centre $rowMatch 5

function ToScreen($point) { return @(($c[0] + $point[0] * $zoom), ($c[1] + $point[1] * $zoom)) }
$gearPt = ToScreen $gear
$row1Pt = ToScreen $row1
$row2Pt = ToScreen $row2
Write-Host ("gear {0},{1}   row1 {2},{3}   row2 {4},{5}" -f $gearPt[0], $gearPt[1], $row1Pt[0], $row1Pt[1], $row2Pt[0], $row2Pt[1])

[void][MouseTest4]::SetForegroundWindow($h)
[MouseTest4]::Move($gearPt[0], $gearPt[1])
Start-Sleep -Milliseconds 800

if ($NoClick) {
    & (Join-Path $PSScriptRoot 'screenshot.ps1') -Out $Out -Pad 10
    exit 0
}

Write-Output '--- click gear ---'
[MouseTest4]::Click()
Start-Sleep -Milliseconds 1000
& (Join-Path $PSScriptRoot 'screenshot.ps1') -Out $Out -Pad 10
Write-Host "captured: $Out"

if ($Bg) {
    # Open the Background fly-out (4th row) and capture it.
    $seg = [regex]::Match($probe, 'rows=([^\s]+)').Groups[1].Value
    $rects = @($seg -split '\|' | Where-Object { $_ -match '^\d+,\d+,\d+x\d+$' -and $_ -ne '0,0,0x0' })
    if ($rects.Count -lt 4) { Write-Output 'FAIL: fewer than 4 rows in the fly-out'; exit 1 }
    $parts = $rects[3] -split '[,x]'
    $row4 = @(([int]$parts[0] + [int]$parts[2] / 2), ([int]$parts[1] + [int]$parts[3] / 2))
    $row4Pt = ToScreen $row4
    Write-Output '--- click row 4 (Background) ---'
    [MouseTest4]::Move($row4Pt[0], $row4Pt[1])
    Start-Sleep -Milliseconds 700
    [MouseTest4]::Click()
    Start-Sleep -Milliseconds 1200
    & (Join-Path $PSScriptRoot 'screenshot.ps1') -Out 'shot-settings-bg.png' -Pad 10
    Write-Host 'captured: shot-settings-bg.png'
    Get-Content $logPath -Tail 6
    exit 0
}

Write-Output '--- click row 1 (Always on top) ---'
[MouseTest4]::Move($row1Pt[0], $row1Pt[1])
Start-Sleep -Milliseconds 600
[MouseTest4]::Click()
Start-Sleep -Milliseconds 1200
Write-Host ("topmost now: {0}" -f [MouseTest4]::IsTopMost($h))

Write-Output '--- click row 2 (Pin to desktop) ---'
[MouseTest4]::Move($row2Pt[0], $row2Pt[1])
Start-Sleep -Milliseconds 600
[MouseTest4]::Click()
Start-Sleep -Milliseconds 1200
$c = [MouseTest4]::ClientOnScreen($h)
Write-Host ("after pin: {0}x{1} at {2},{3}  topmost={4}" -f $c[2], $c[3], $c[0], $c[1], [MouseTest4]::IsTopMost($h))

[void][MouseTest4]::SetCursorPos(960, 540)
Write-Output '--- panel log tail ---'
Get-Content $logPath -Tail 16

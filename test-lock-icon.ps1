# Drives the panel's hover lock button with real mouse input: hovers the
# bottom-right corner, captures a screenshot, clicks to unlock, then clicks
# again to lock. Used to verify the in-page button end to end.
param([switch]$NoClick, [switch]$Escape, [string]$Out = 'shot-panel-lock.png')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MouseTest
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
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }
    public struct POINT { public int X, Y; }

    public static IntPtr Best(uint pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            if (GetWindowTextLength(h) == 0) return true;
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

    public static void Click()
    {
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(90);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

$proc = Get-Process -Name 'iCloudPanel' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: iCloudPanel.exe is not running'; exit 1 }
$h = [MouseTest]::Best([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: no panel window'; exit 1 }

function Show-Style {
    $style = [MouseTest]::GetWindowLong($h, -16)
    $c = [MouseTest]::ClientOnScreen($h)
    $framed = ($style -band 0x00C00000) -ne 0
    Write-Output ('style: 0x{0:X8}  caption={1}  page: {2}x{3} at {4},{5}' -f $style, $framed, $c[2], $c[3], $c[0], $c[1])
}

Write-Output '--- before hover ---'
Show-Style

[void][MouseTest]::SetForegroundWindow($h)
$c = [MouseTest]::ClientOnScreen($h)
$cornerX = $c[0] + $c[2] - 25
$cornerY = $c[1] + $c[3] - 25

# Two nudges so the page sees a real mousemove in the corner zone.
[void][MouseTest]::SetCursorPos($cornerX - 4, $cornerY - 4)
Start-Sleep -Milliseconds 350
[void][MouseTest]::SetCursorPos($cornerX, $cornerY)
Start-Sleep -Milliseconds 900

& (Join-Path $PSScriptRoot 'screenshot.ps1') -Out $Out -Pad 10
Write-Output "captured: $Out (cursor parked bottom-right; button should be visible)"

if ($NoClick) { exit 0 }

Write-Output '--- click 1 (unlock) ---'
[MouseTest]::Click()
Start-Sleep -Milliseconds 1600
Show-Style

if ($Escape) {
    Write-Output '--- Escape (should lock again) ---'
    $shell = New-Object -ComObject WScript.Shell
    $shell.SendKeys('{ESC}')
    Start-Sleep -Milliseconds 1600
    Show-Style
    [void][MouseTest]::SetCursorPos(960, 540)
    Write-Output '--- panel log tail ---'
    Get-Content (Join-Path $PSScriptRoot 'panel\panel.log') -Tail 5
    exit 0
}

$c2 = [MouseTest]::ClientOnScreen($h)
[void][MouseTest]::SetCursorPos($c2[0] + $c2[2] - 25, $c2[1] + $c2[3] - 25)
Start-Sleep -Milliseconds 900
Write-Output '--- click 2 (lock again) ---'
[MouseTest]::Click()
Start-Sleep -Milliseconds 1600
Show-Style

# Park the cursor somewhere harmless.
[void][MouseTest]::SetCursorPos(960, 540)
Write-Output '--- panel log tail ---'
Get-Content (Join-Path $PSScriptRoot 'panel\panel.log') -Tail 8

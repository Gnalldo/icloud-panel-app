# Tests whether the clip region survives: sets it, captures, waits, captures again.
param([int]$Wait = 1200)

$ErrorActionPreference = 'Stop'
$appDir     = $PSScriptRoot
$profileDir = Join-Path $appDir 'profile'
$pad        = 12

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ClipTest
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    public struct RECT { public int L, T, R, B; }
}
'@
[void][ClipTest]::SetProcessDPIAware()

$procs = @(Get-CimInstance Win32_Process -Filter "Name='brave.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profileDir*" })
$hwnd = [IntPtr]::Zero
foreach ($proc in $procs) {
    $gp = Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue
    if ($gp -and $gp.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $gp.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output 'FAIL: no window'; exit 1 }

[void][ClipTest]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$rect = New-Object ClipTest+RECT
[void][ClipTest]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.R - $rect.L
$h = $rect.B - $rect.T
$cr = New-Object ClipTest+RECT
[void][ClipTest]::GetClientRect($hwnd, [ref]$cr)
Write-Output "window: ${w}x${h} at $($rect.L),$($rect.T)  client: $($cr.R)x$($cr.B)  style: 0x$('{0:X8}' -f [ClipTest]::GetWindowLong($hwnd, -16))"

function Set-PageRegion {
    $x1 = 7
    $y1 = 30
    $x2 = 7 + ($w - 14) + 1
    $y2 = 30 + ($h - 37) + 1
    $rgn = [ClipTest]::CreateRoundRectRgn($x1, $y1, $x2, $y2, 28, 28)
    $res = [ClipTest]::SetWindowRgn($hwnd, $rgn, $true)
    Write-Output "SetWindowRgn(7,30 - $x2,$y2) result: $res (0 = failure)"
}

function Capture([string]$name) {
    $bw = $w + 2 * $pad
    $bh = $h + 2 * $pad
    $bmp = New-Object System.Drawing.Bitmap($bw, $bh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($rect.L - $pad), ($rect.T - $pad), 0, 0, $bmp.Size)
    $g.Dispose()

    $px = @(2, [int]($w / 2), 2, ($w - 3), 2)
    $py = @(2, 2, [int]($h / 2), [int]($h / 2), ($h - 4))
    $parts = @()
    for ($i = 0; $i -lt $px.Count; $i++) {
        $c = $bmp.GetPixel(($pad + $px[$i]), ($pad + $py[$i]))
        $parts += ("({0},{1})=rgb({2},{3},{4})" -f $px[$i], $py[$i], $c.R, $c.G, $c.B)
    }
    $bmp.Save((Join-Path $appDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "$name : $($parts -join '  ')"
}

Set-PageRegion
Capture 'shot-immediate.png'
Start-Sleep -Milliseconds $Wait
Write-Output "visible after wait: $([ClipTest]::IsWindowVisible($hwnd))"
Capture 'shot-after-wait.png'
Set-PageRegion
Write-Output 'region re-applied at end'

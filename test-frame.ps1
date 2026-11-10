# Experiment: try to make Brave's app window truly borderless.
# 1) collapse the DWM extended frame (margins 0)
# 2) stretch the page child window to the whole client area
# and capture the result for visual review.
$ErrorActionPreference = 'Stop'

$appDir     = $PSScriptRoot
$profileDir = Join-Path $appDir 'profile'
$pad        = 16

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FrameTest
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }
    public struct MARGINS { public int L, R, T, B; }

    public static IntPtr PageChild(IntPtr parent)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (sb.ToString().IndexOf("RenderWidgetHost", StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long a = (long)(r.R - r.L) * (r.B - r.T);
            if (a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@
[void][FrameTest]::SetProcessDPIAware()

$procs = @(Get-CimInstance Win32_Process -Filter "Name='brave.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profileDir*" })
$hwnd = [IntPtr]::Zero
foreach ($proc in $procs) {
    $gp = Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue
    if ($gp -and $gp.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $gp.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output 'FAIL: no window'; exit 1 }

[void][FrameTest]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$wr = New-Object FrameTest+RECT
[void][FrameTest]::GetWindowRect($hwnd, [ref]$wr)
$w = $wr.R - $wr.L
$h = $wr.B - $wr.T

$cr = New-Object FrameTest+RECT
[void][FrameTest]::GetClientRect($hwnd, [ref]$cr)
Write-Output "BEFORE  window ${w}x${h}  client $($cr.R)x$($cr.B)  insets L/R/B: $($cr.L)/$($w - $cr.R)/$($h - $cr.B)"

# 1) collapse the extended DWM frame
$m = New-Object FrameTest+MARGINS
$m.L = 0; $m.R = 0; $m.T = 0; $m.B = 0
$hr = [FrameTest]::DwmExtendFrameIntoClientArea($hwnd, [ref]$m)
Write-Output "DwmExtendFrameIntoClientArea(0,0,0,0) -> $hr (0 = ok)"
Start-Sleep -Milliseconds 250
[void][FrameTest]::GetWindowRect($hwnd, [ref]$wr)
[void][FrameTest]::GetClientRect($hwnd, [ref]$cr)
$w = $wr.R - $wr.L
$h = $wr.B - $wr.T
Write-Output "AFTER-FRAME  window ${w}x${h}  client $($cr.R)x$($cr.B)  insets L/R/B: $($cr.L)/$($w - $cr.R)/$($h - $cr.B)"

# 2) stretch the page child to the full client area
$page = [FrameTest]::PageChild($hwnd)
if ($page -eq [IntPtr]::Zero) { Write-Output 'no RenderWidgetHost child found'; exit 1 }
$pr = New-Object FrameTest+RECT
[void][FrameTest]::GetWindowRect($page, [ref]$pr)
Write-Output "page child before: ($($pr.L - $wr.L),$($pr.T - $wr.T)) $($pr.R - $pr.L)x$($pr.B - $pr.T)"
$flags = 0x0004 -bor 0x0010            # SWP_NOZORDER | SWP_NOACTIVATE
[void][FrameTest]::SetWindowPos($page, [IntPtr]::Zero, 0, 0, $cr.R, $cr.B, $flags)
Start-Sleep -Milliseconds 700
[void][FrameTest]::GetWindowRect($page, [ref]$pr)
Write-Output "page child after:  ($($pr.L - $wr.L),$($pr.T - $wr.T)) $($pr.R - $pr.L)x$($pr.B - $pr.T)"

# capture
$bmp = New-Object System.Drawing.Bitmap(($w + 2 * $pad), ($h + 2 * $pad))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(($wr.L - $pad), ($wr.T - $pad), 0, 0, $bmp.Size)
$g.Dispose()

$px = @(1, [int]($w / 2), 1, ($w - 2), 1, [int]($w / 2))
$py = @(1, 1, [int]($h / 2), [int]($h / 2), ($h - 2), [int]($h / 2))
$parts = @()
for ($i = 0; $i -lt $px.Count; $i++) {
    $c = $bmp.GetPixel(($pad + $px[$i]), ($pad + $py[$i]))
    $parts += ("({0},{1})=rgb({2},{3},{4})" -f $px[$i], $py[$i], $c.R, $c.G, $c.B)
}
$outPath = Join-Path $appDir 'shot-frame.png'
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "probes: $($parts -join '  ')"
Write-Output "saved: $outPath"

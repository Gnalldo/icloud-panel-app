# Captures a top-level window (default: the WebView2 iCloud panel) to a PNG.
param(
    [string]$ProcessName = 'iCloudPanel.exe',
    [string]$Out = 'shot-panel.png',
    [int]$Pad = 24,
    # The panel normally runs inside a PowerShell host, not its own exe.
    [string]$HostScript = 'iCloudPanelHost.ps1'
)

$ErrorActionPreference = 'Stop'
$appDir = $PSScriptRoot

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Shot
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
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
            if (!IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            // Only the WinForms form counts - the PowerShell host also owns a
            // console window that must never be captured instead of the panel.
            StringBuilder cls = new StringBuilder(256);
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
}
'@
[void][Shot]::SetProcessDPIAware()

$leaf = [System.IO.Path]::GetFileNameWithoutExtension($ProcessName)
$pids = @(Get-Process -Name $leaf -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
if ($pids.Count -eq 0 -and $HostScript) {
    $pids = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -eq 'powershell.exe' -and $_.CommandLine -like ('*' + $HostScript + '*')
    } | ForEach-Object { [uint32]$_.ProcessId })
}
if ($pids.Count -eq 0) { Write-Output "FAIL: the panel is not running"; exit 1 }

$hwnd = [Shot]::Best([uint32[]]$pids)
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "FAIL: no visible window for $ProcessName"; exit 1 }

[void][Shot]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 700

$rect = New-Object Shot+RECT
if (-not [Shot]::GetWindowRect($hwnd, [ref]$rect)) { Write-Output 'FAIL: GetWindowRect'; exit 1 }
$w  = $rect.R - $rect.L
$ht = $rect.B - $rect.T

$bmp = New-Object System.Drawing.Bitmap(($w + 2 * $Pad), ($ht + 2 * $Pad))
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(($rect.L - $Pad), ($rect.T - $Pad), 0, 0, $bmp.Size)
$g.Dispose()

$path = Join-Path $appDir $Out
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "window: ${w}x${ht} at $($rect.L),$($rect.T)"
Write-Output "saved: $path"

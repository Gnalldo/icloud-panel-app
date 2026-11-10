# Checks the pinned layer behaviour end to end:
#   1. after a tray-style show, the panel has focus (so it is on top)
#   2. clicking another window's title bar drops the panel back behind it
# Real mouse input is used for the focus change, so Windows' foreground rules
# do not interfere.
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class PinDrop
{
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr extra);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }

    public static List<IntPtr> ZOrder()
    {
        List<IntPtr> list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (IsWindowVisible(h) && GetWindowTextLength(h) > 0) { list.Add(h); }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr BestForPid(uint pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        foreach (IntPtr h in ZOrder())
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid) continue;
            RECT r;
            if (!GetWindowRect(h, out r)) continue;
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea) { bestArea = area; best = h; }
        }
        return best;
    }

    public static int IndexOf(IntPtr h)
    {
        List<IntPtr> list = ZOrder();
        for (int i = 0; i < list.Count; i++) { if (list[i] == h) return i; }
        return -1;
    }

    public static int Count() { return ZOrder().Count; }

    public static string Title(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        StringBuilder sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        return sb.ToString();
    }

    public static bool RectOf(IntPtr h, out RECT r) { return GetWindowRect(h, out r); }
    public static bool IsTopMost(IntPtr h) { return (GetWindowLong(h, -20) & 0x00000008) != 0; }

    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(90);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

function Line($text) { if (-not $Quiet) { Write-Host $text } }

$proc = Get-Process -Name 'iCloudPanel' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: iCloudPanel.exe is not running'; exit 1 }
$panel = [PinDrop]::BestForPid([uint32]$proc.Id)
if ($panel -eq [IntPtr]::Zero) { Write-Output 'FAIL: no panel window'; exit 1 }

$pinned = ((Get-Content (Join-Path $PSScriptRoot 'config.json') -Raw) -match '"pinned"\s*:\s*true')

# ---- 1. raised? (the command already did a tray-style show) ---------------
$fg = [PinDrop]::GetForegroundWindow()
$raised = ($fg -eq $panel)
Line ("pinned={0}  topmost={1}  foreground is panel={2}  panel z-index={3}" -f `
    $pinned, [PinDrop]::IsTopMost($panel), $raised, [PinDrop]::IndexOf($panel))

# ---- 2. pick another real window and click its title bar ------------------
$target = [IntPtr]::Zero
$tr = New-Object PinDrop+RECT
foreach ($h in [PinDrop]::ZOrder()) {
    if ($h -eq $panel) { continue }
    $r = New-Object PinDrop+RECT
    if (-not [PinDrop]::RectOf($h, [ref]$r)) { continue }
    if (($r.R - $r.L) -lt 400 -or ($r.B - $r.T) -lt 200) { continue }
    $target = $h
    $tr = $r
    break
}
if ($target -eq [IntPtr]::Zero) { Write-Output 'FAIL: no other window to focus'; exit 1 }
Line ("clicking title bar of '{0}' at {1},{2}" -f [PinDrop]::Title($target), [int](($tr.L + $tr.R) / 2), ($tr.T + 12))

[PinDrop]::ClickAt([int](($tr.L + $tr.R) / 2), ($tr.T + 12))
Start-Sleep -Milliseconds 1400

# ---- 3. verdict ----------------------------------------------------------
$fg2 = [PinDrop]::GetForegroundWindow()
$panelIndex = [PinDrop]::IndexOf($panel)
$targetIndex = [PinDrop]::IndexOf($target)
$dropped = ($fg2 -ne $panel) -and ($panelIndex -gt $targetIndex)

Line ("after click: foreground='{0}'  panel z-index={1}  target z-index={2}  total windows={3}" -f `
    [PinDrop]::Title($fg2), $panelIndex, $targetIndex, [PinDrop]::Count())
Write-Output ("VERDICT raised={0} droppedBehind={1}" -f $raised, $dropped)

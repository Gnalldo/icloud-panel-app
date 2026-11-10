# Lists all top-level windows owned by a process, including hidden ones.
param([string]$ProcessName = 'iCloudPanel')

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WinDiag
{
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int L, T, R, B; }

    public static string[] List(uint pid)
    {
        List<string> rows = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner != pid) return true;
            StringBuilder cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            StringBuilder title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            RECT r;
            GetWindowRect(h, out r);
            rows.Add(string.Format("hwnd={0} class={1} visible={2} owned={3} rect={4},{5} {6}x{7} title='{8}'",
                h, cls, IsWindowVisible(h), GetWindow(h, 4) != IntPtr.Zero,
                r.L, r.T, r.R - r.L, r.B - r.T, title));
            return true;
        }, IntPtr.Zero);
        return rows.ToArray();
    }
}
'@

foreach ($p in (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
    Write-Output "--- $($p.ProcessName) pid=$($p.Id) ---"
    foreach ($row in [WinDiag]::List([uint32]$p.Id)) { Write-Output $row }
}

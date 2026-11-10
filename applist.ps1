param([switch]$Force)
$ErrorActionPreference = 'SilentlyContinue'
$cache = Join-Path $PSScriptRoot 'panel\appcache.json'
if (-not $Force -and (Test-Path $cache)) {
    $age = (Get-Date) - (Get-Item $cache).LastWriteTime
    if ($age.TotalHours -lt 24) { Get-Content -Raw $cache; exit 0 }
}
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEINFO {
    public IntPtr hIcon; public int iIcon; public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
}
public static class ShellIcon {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr pv);
}
'@
function Icon-Uri {
    param([IntPtr]$Pidl = [IntPtr]::Zero, [string]$Path = '')
    $shfi = New-Object SHFILEINFO
    $size = [Runtime.InteropServices.Marshal]::SizeOf($shfi)
    if ($Pidl -ne [IntPtr]::Zero) {
        $ok = [ShellIcon]::SHGetFileInfo($Pidl, 0, [ref]$shfi, $size, 0x108)
    } elseif ($Path) {
        $ok = [ShellIcon]::SHGetFileInfo($Path, 0, [ref]$shfi, $size, 0x100)
    } else { return '' }
    if ($ok -eq [IntPtr]::Zero -or $shfi.hIcon -eq [IntPtr]::Zero) { return '' }
    try {
        $icon = [System.Drawing.Icon]::FromHandle($shfi.hIcon)
        $bmp = $icon.ToBitmap()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose(); $icon.Dispose()
        return 'data:image/png;base64,' + [Convert]::ToBase64String($ms.ToArray())
    } finally {
        [ShellIcon]::DestroyIcon($shfi.hIcon) | Out-Null
    }
}
function AppsFolder-Icon([string]$id) {
    $pidl = [IntPtr]::Zero; $attrs = 0
    $hr = [ShellIcon]::SHParseDisplayName("shell:AppsFolder\$id", [IntPtr]::Zero, [ref]$pidl, 0, [ref]$attrs)
    if ($hr -ne 0 -or $pidl -eq [IntPtr]::Zero) { return '' }
    try { return Icon-Uri -Pidl $pidl } finally { [ShellIcon]::CoTaskMemFree($pidl) }
}
function Shortcut-Icon([string]$name) {
    $roots = @((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
               (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $lnk = Get-ChildItem -Path $root -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue |
               Where-Object { $_.BaseName -ieq $name } | Select-Object -First 1
        if ($lnk) { return Icon-Uri -Path $lnk.FullName }
    }
    return ''
}
$apps = New-Object System.Collections.ArrayList
$seen = @{}
foreach ($a in (Get-StartApps | Sort-Object Name)) {
    $id = [string]$a.AppID
    $name = [string]$a.Name
    if (-not $id -or -not $name -or $seen.ContainsKey($id)) { continue }
    $seen[$id] = $true
    $icon = AppsFolder-Icon $id
    if (-not $icon) { $icon = Shortcut-Icon $name }
    [void]$apps.Add([pscustomobject]@{ id = $id; name = $name; icon = $icon })
    if ($apps.Count -ge 260) { break }
}
$json = ConvertTo-Json -InputObject @($apps) -Compress -Depth 4
if (-not $json) { $json = '[]' }
[IO.File]::WriteAllText($cache, $json, (New-Object Text.UTF8Encoding($false)))
Write-Output $json

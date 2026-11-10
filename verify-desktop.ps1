# Verifies the desktop shortcut and asks Explorer to refresh its icons.
$ErrorActionPreference = 'Stop'

$desktop = [Environment]::GetFolderPath('Desktop')
$lnk     = Join-Path $desktop 'iCloud.lnk'
$icon    = Join-Path $PSScriptRoot 'icloud.ico'

Write-Output "desktop:   $desktop"
Write-Output "shortcut:  $lnk"
Write-Output "exists:    $(Test-Path $lnk)"

if (Test-Path $lnk) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    Write-Output "target:    $($s.TargetPath)"
    Write-Output "args:      $($s.Arguments)"
    Write-Output "icon:      $($s.IconLocation)"
}

Add-Type -AssemblyName System.Drawing
$ico = New-Object System.Drawing.Icon($icon)
Write-Output "icon file: $icon ($($ico.Width)x$($ico.Height))"
$ico.Dispose()

Add-Type -Namespace Shell32 -Name Native -MemberDefinition @'
[DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
'@
[Shell32.Native]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output 'shell refresh: signalled'

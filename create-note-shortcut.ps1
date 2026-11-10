# Start-menu shortcut that opens a new iCloud note in the panel that is already
# running (or starts it). The panel does the clicking, so the shortcut itself is
# just the hidden PowerShell trampoline. Re-run this any time to recreate it.
$ErrorActionPreference = 'Stop'

$appDir  = $PSScriptRoot
$icon    = Join-Path $appDir 'icloud.ico'
$vbs     = Join-Path $appDir 'start-new-note.vbs'
$wscript = Join-Path $env:WINDIR 'System32\wscript.exe'
$target  = Join-Path ([Environment]::GetFolderPath('Programs')) 'New iCloud Note.lnk'

if (-not (Test-Path $vbs)) { throw "missing launcher: $vbs" }
if (-not (Test-Path $icon)) { throw "missing icon: $icon" }

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($target)
$shortcut.TargetPath       = $wscript
$shortcut.Arguments        = "`"$vbs`""
$shortcut.WorkingDirectory = $appDir
$shortcut.IconLocation     = "$icon,0"
$shortcut.Description      = 'Create a new note in iCloud Notes'
# Minimised, like the tray entry: wscript itself never draws a window.
$shortcut.WindowStyle      = 7
$shortcut.Save()

Write-Output "created: $target  ->  $wscript $vbs"
Write-Output 'Right-click it in Start > All apps > Pin to Start for a tile (Windows does not allow scripting that part).'

# Creates standalone "iCloud" app shortcuts that launch iCloud in a Brave
# app window (no tabs, no address bar) using its own profile, so it runs as an
# independent app but reuses the Brave you already have installed.
$ErrorActionPreference = 'Stop'

$appDir     = $PSScriptRoot
$profileDir = Join-Path $appDir 'profile'
$icon       = Join-Path $appDir 'icloud.ico'

# Brave first, then any other Chromium browser as a fallback.
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'BraveSoftware\Brave-Browser\Application\brave.exe')
    'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe'
    'C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe'
    'C:\Program Files\Google\Chrome\Application\chrome.exe'
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)
$browser = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'No Brave/Chromium browser found.' }

New-Item -ItemType Directory -Force -Path $profileDir | Out-Null

# Shortcuts go through the launcher so the panel opens frameless and docked.
$launcher  = Join-Path $env:WINDIR 'System32\wscript.exe'
$appVbs    = Join-Path $appDir 'start-panel.vbs'
$arguments = "`"$appVbs`""

$shell = New-Object -ComObject WScript.Shell

$targets = @(
    (Join-Path $appDir 'iCloud.lnk')
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'iCloud.lnk')
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'iCloud.lnk')
)

foreach ($target in $targets) {
    $shortcut = $shell.CreateShortcut($target)
    $shortcut.TargetPath       = $launcher
    $shortcut.Arguments        = $arguments
    $shortcut.WorkingDirectory = $appDir
    $shortcut.IconLocation     = "$icon,0"
    $shortcut.Description      = 'iCloud (standalone app window)'
    $shortcut.WindowStyle      = 1
    $shortcut.Save()
    Write-Output "created: $target  ->  $launcher $arguments"
}

# Startup entry so the tray icon is always available after signing in.
$wscript = Join-Path $env:WINDIR 'System32\wscript.exe'
$trayVbs = Join-Path $appDir 'start-panel-tray.vbs'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'iCloud Tray.lnk'
$tray = $shell.CreateShortcut($startup)
$tray.TargetPath       = $wscript
$tray.Arguments        = "`"$trayVbs`""
$tray.WorkingDirectory = $appDir
$tray.IconLocation     = "$icon,0"
$tray.Description      = 'iCloud tray icon (show/hide the iCloud app)'
$tray.WindowStyle      = 7
$tray.Save()
Write-Output "created: $startup  ->  $wscript"

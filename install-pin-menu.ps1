$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$script = Join-Path $root 'pin-to-dash.ps1'
$icon = Join-Path $root 'icloud.ico'
$cmd = '"powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "' + $script + '" "%1"'
foreach ($base in @('Software\Classes\*\shell\DashLauncher','Software\Classes\Directory\shell\DashLauncher')) {
    $k = New-Item -Path ('HKCU:\' + $base) -Force
    New-ItemProperty -Path $k.PSPath -Name '(default)' -Value 'Pin to DashLauncher' -PropertyType String -Force | Out-Null
    if (Test-Path $icon) { New-ItemProperty -Path $k.PSPath -Name 'Icon' -Value $icon -PropertyType String -Force | Out-Null }
    $c = New-Item -Path ($k.PSPath + '\command') -Force
    New-ItemProperty -Path $c.PSPath -Name '(default)' -Value $cmd -PropertyType String -Force | Out-Null
}
Write-Output 'EXPLORER MENU OK'

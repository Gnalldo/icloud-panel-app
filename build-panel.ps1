# Rebuilds panel\iCloudPanel.exe from iCloudPanel.cs with the .NET Framework compiler.
$ErrorActionPreference = 'Stop'

$appDir = $PSScriptRoot
$panel  = Join-Path $appDir 'panel'
$source = Join-Path $appDir 'iCloudPanel.cs'
$target = Join-Path $panel 'iCloudPanel.exe'
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc))    { throw "csc.exe not found: $csc" }
if (-not (Test-Path $source)) { throw "source not found: $source" }

$core  = Join-Path $panel 'Microsoft.Web.WebView2.Core.dll'
$forms = Join-Path $panel 'Microsoft.Web.WebView2.WinForms.dll'
if (-not (Test-Path $core) -or -not (Test-Path $forms)) {
    throw 'WebView2 SDK assemblies missing. Run setup-webview2.ps1 first.'
}

# The exe cannot be replaced while the panel is running.
$running = @(Get-Process -Name 'iCloudPanel' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Output "stopping running panel (pid $($running.Id -join ', '))"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

$compilerArgs = @(
    '-nologo'
    '-target:winexe'
    '-platform:x64'
    "-out:$target"
    "-r:$core"
    "-r:$forms"
    '-r:System.dll'
    '-r:System.Core.dll'
    '-r:System.Drawing.dll'
    '-r:System.Windows.Forms.dll'
    $source
)

& $csc $compilerArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

$info = Get-Item $target
Write-Output "built: $target ($($info.Length) bytes)"
Write-Output 'the panel restarts on the next Show / tray click'

# Downloads the WebView2 SDK (NuGet) and stages the assemblies the panel needs.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$appDir = $PSScriptRoot
$panel  = Join-Path $appDir 'panel'
New-Item -ItemType Directory -Force -Path $panel | Out-Null

$index = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/index.json'
$version = ($index.versions | Where-Object { $_ -notmatch '-' })[-1]
Write-Output "nuget version: $version"

$nupkg = Join-Path $appDir 'webview2.nupkg'
if (-not (Test-Path $nupkg)) {
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg"
    Invoke-WebRequest $url -OutFile $nupkg
}
Write-Output "package bytes: $((Get-Item $nupkg).Length)"

$tmp = Join-Path $appDir 'wv2tmp'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
$zip = Join-Path $appDir 'webview2.zip'
Copy-Item $nupkg $zip -Force
Expand-Archive -Path $zip -DestinationPath $tmp -Force

function Find-Assembly([string]$leaf) {
    Get-ChildItem $tmp -Recurse -Filter $leaf |
        Where-Object { $_.FullName -match '\\lib\\net' } |
        Sort-Object FullName |
        Select-Object -First 1
}

$core = Find-Assembly 'Microsoft.Web.WebView2.Core.dll'
$forms = Find-Assembly 'Microsoft.Web.WebView2.WinForms.dll'
$loader = Get-ChildItem $tmp -Recurse -Filter 'WebView2Loader.dll' |
    Where-Object { $_.FullName -match 'win-x64' -and $_.FullName -match 'native' } | Select-Object -First 1

if (-not $core) { throw 'Core assembly not found in package' }
if (-not $forms) { throw 'WinForms assembly not found in package' }
if (-not $loader) { throw 'WebView2Loader.dll (win-x64) not found in package' }

Copy-Item $core.FullName $panel -Force
Copy-Item $forms.FullName $panel -Force
Copy-Item $loader.FullName $panel -Force

Get-ChildItem $panel | Select-Object Name, Length | Format-Table -AutoSize

$pv = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue).pv
if (-not $pv) {
    $pv = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue).pv
}
if (-not $pv) { $pv = (Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue).pv }
Write-Output "WebView2 runtime version: $(if ($pv) { $pv } else { 'NOT FOUND' })"
Write-Output "csc available: $(Test-Path 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')"

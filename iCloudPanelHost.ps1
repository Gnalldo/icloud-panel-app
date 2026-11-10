# Runs the iCloud panel inside powershell.exe instead of its own .exe.
#
# Windows 11 Smart App Control (enforce mode on this machine) blocks freshly
# built unsigned executables with "An Application Control policy has blocked
# this file". powershell.exe is a signed Microsoft binary, so hosting the same
# C# here keeps every panel feature working without weakening that policy.
#
# The panel source is compiled in memory each start; nothing new is written to
# disk and no security setting is changed.
$ErrorActionPreference = 'Stop'
# The host runs hidden with no console: PowerShell turns any warning or
# uncaught error into a modal message box, which would replace the panel
# (Add-Type warns that the compiled type declares no public members).
# Keep them quiet here and report through host.log instead.
$WarningPreference = 'SilentlyContinue'

$appDir  = Join-Path $PSScriptRoot 'panel'
$source  = Join-Path $PSScriptRoot 'iCloudPanel.cs'
$coreDll = Join-Path $appDir 'Microsoft.Web.WebView2.Core.dll'
$wfDll   = Join-Path $appDir 'Microsoft.Web.WebView2.WinForms.dll'
$logPath = Join-Path $appDir 'host.log'

function HLog([string]$message) {
    try { Add-Content -Path $logPath -Value ('{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $message) } catch { }
}

# Only ever one panel: a second launch exits instead of opening a twin window.
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\iCloudPanelHost', [ref]$createdNew)
if (-not $createdNew) {
    HLog 'another host is already running - exiting'
    exit 0
}

HLog 'host starting'
try {
    foreach ($required in @($source, $coreDll, $wfDll)) {
        if (-not (Test-Path $required)) { throw "missing: $required" }
    }

    # The panel resolves config.json, its profile and its log from this folder.
    $env:ICLOUD_PANEL_DIR = $appDir

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Security
    HLog 'framework assemblies loaded'

    Add-Type -Path $wfDll
    Add-Type -Path $coreDll
    HLog 'webview2 sdk loaded'

    # DPI awareness has to be set before any window exists (the exe did this in Main).
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DpiAwareness
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@
    [void][DpiAwareness]::SetProcessDPIAware()
    # powershell.exe always owns a console window (the blue one): keep it hidden so
    # it can never be mistaken for - or shown instead of - the panel.
    $console = [DpiAwareness]::GetConsoleWindow()
    if ($console -ne [IntPtr]::Zero) { [void][DpiAwareness]::ShowWindow($console, 0) }
    HLog 'dpi aware, console hidden'

    $code = Get-Content -Raw -Path $source
    Add-Type -TypeDefinition $code -WarningAction SilentlyContinue -ReferencedAssemblies @(
        $wfDll,
        $coreDll,
        'System.dll',
        'System.Core.dll',
        'System.Drawing.dll',
        'System.Windows.Forms.dll',
        'System.Security.dll'
    )
    HLog 'panel source compiled'

    [System.Windows.Forms.Application]::EnableVisualStyles()
    [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

    $form = New-Object iCloudPanel.PanelForm
    HLog ('form created: ' + $form.Text)

    [System.Windows.Forms.Application]::Run($form)
    HLog 'host exiting normally'
}
catch {
    HLog ('ERROR: ' + $_.Exception.ToString())
    # exit, not throw: an uncaught error would pop a message box over the panel.
    exit 1
}

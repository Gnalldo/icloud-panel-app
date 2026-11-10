' Shows the iCloud WebView2 panel (no console window).
Option Explicit
Dim fso, shell, scriptDir, ps1, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = scriptDir & "\iCloudPanelTray.ps1"
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """ -Show"
shell.Run cmd, 0, False

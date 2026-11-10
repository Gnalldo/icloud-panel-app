' Opens the iCloud panel with the frameless/docked styling applied, no console.
Option Explicit
Dim fso, shell, scriptDir, ps1, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = scriptDir & "\iCloudTray.ps1"
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """ -Show"
shell.Run cmd, 0, False

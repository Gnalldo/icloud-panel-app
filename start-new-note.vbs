' Opens the iCloud panel and creates a new note in it (no console window).
Option Explicit
Dim fso, shell, dir, ps1, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = dir & "\iCloudPanelTray.ps1"
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """ -NewNote"
shell.Run cmd, 0, False

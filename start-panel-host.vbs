' Starts the iCloud panel hosted by PowerShell (no console window).
Option Explicit
Dim fso, shell, dir, ps1, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = dir & "\iCloudPanelHost.ps1"
cmd = "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """"
shell.Run cmd, 0, False

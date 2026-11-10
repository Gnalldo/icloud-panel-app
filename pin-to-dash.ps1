param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
$queue = Join-Path $PSScriptRoot 'panel\pin-queue.txt'
$dir = Split-Path -Parent $queue
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$clean = ($Path -replace '[\r\n]+', ' ').Trim()
if (-not $clean) { exit 1 }
[IO.File]::AppendAllText($queue, $clean + "`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Output ('queued: ' + $clean)

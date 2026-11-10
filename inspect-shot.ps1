# Measures colour runs across the screenshot so the window frame can be identified.
param([string]$Path = (Join-Path $PSScriptRoot 'shot.png'))

Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
Write-Output "image: $($bmp.Width) x $($bmp.Height)"
Write-Output 'reference: window 608x888 captured with 28px padding; page origin should be image (35,58)'

function Get-Runs {
    param($colors, [string]$axis)
    $prev = $null
    $start = 0
    for ($i = 0; $i -lt $colors.Count; $i++) {
        $c = $colors[$i]
        if ($null -eq $prev) { $prev = $c; $start = $i; continue }
        $d = [Math]::Abs($c.R - $prev.R) + [Math]::Abs($c.G - $prev.G) + [Math]::Abs($c.B - $prev.B)
        if ($d -gt 24) {
            $len = $i - $start
            if ($len -ge 2) { Write-Output ("{0} {1}-{2} ({3}px) rgb({4},{5},{6})" -f $axis, $start, ($i - 1), $len, $prev.R, $prev.G, $prev.B) }
            $start = $i; $prev = $c
        }
    }
    $len = $colors.Count - $start
    if ($len -ge 2) { Write-Output ("{0} {1}-{2} ({3}px) rgb({4},{5},{6})" -f $axis, $start, ($colors.Count - 1), $len, $prev.R, $prev.G, $prev.B) }
}

foreach ($y in @(300, [int]($bmp.Height / 2))) {
    $colors = @()
    for ($x = 0; $x -lt $bmp.Width; $x++) { $colors += $bmp.GetPixel($x, $y) }
    Write-Output "--- row y=$y ---"
    Get-Runs -colors $colors -axis 'x'
}

foreach ($x in @(300, [int]($bmp.Width / 2))) {
    $colors = @()
    for ($y = 0; $y -lt $bmp.Height; $y++) { $colors += $bmp.GetPixel($x, $y) }
    Write-Output "--- column x=$x ---"
    Get-Runs -colors $colors -axis 'y'
}

Write-Output '--- corner probe (top-left of page area) ---'
foreach ($p in @(@(30,58), @(34,58), @(35,58), @(38,58), @(41,58), @(44,58), @(300,58), @(300,61), @(300,64), @(300,90))) {
    $c = $bmp.GetPixel($p[0], $p[1])
    Write-Output ("({0},{1}) rgb({2},{3},{4})" -f $p[0], $p[1], $c.R, $c.G, $c.B)
}
$bmp.Dispose()

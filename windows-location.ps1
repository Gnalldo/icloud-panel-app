# windows-location.ps1 - the PC's own location, from the Windows location service.
# Prints one JSON object: {ok:true,lat,lon} or {ok:false,error}.
# Never waits longer than -TimeoutMs, so a permission prompt or a dead service
# cannot hold up the panel.
param([int]$TimeoutMs = 8000)

$ErrorActionPreference = 'Stop'

function Reply($ok, $lat, $lon, $error) {
    if ($ok) {
        [ordered]@{ ok = $true; lat = [math]::Round($lat, 4); lon = [math]::Round($lon, 4) } |
            ConvertTo-Json -Compress
    } else {
        [ordered]@{ ok = $false; error = $error } | ConvertTo-Json -Compress
    }
}

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Devices.Geolocation.Geolocator, Windows.Devices.Geolocation, ContentType = WindowsRuntime]
    $geo = New-Object Windows.Devices.Geolocation.Geolocator
    try { $geo.DesiredAccuracyInMeters = 3000 } catch { }

    $op = $geo.GetGeopositionAsync()
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]
    if ($null -eq $asTask) { Reply $false $null $null 'unsupported'; exit 0 }

    $task = $asTask.MakeGenericMethod([Windows.Devices.Geolocation.Geoposition]).Invoke($null, @($op))
    if (-not $task.Wait($TimeoutMs)) { Reply $false $null $null 'timeout'; exit 0 }

    $pos = $task.Result
    $p = $pos.Coordinate.Point.Position
    Reply $true $p.Latitude $p.Longitude $null
}
catch [System.UnauthorizedAccessException] {
    Reply $false $null $null 'denied'
}
catch {
    $m = $_.Exception.Message
    if ($m -match 'disabled|Location is off|consent') { Reply $false $null $null 'disabled' }
    else { Reply $false $null $null ('error: ' + $m) }
}

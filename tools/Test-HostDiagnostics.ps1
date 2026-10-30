#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath)

$ErrorActionPreference = 'Stop'
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path

# Read-only capture and rejection tests only; no eligible removal or disable request is sent.
function Invoke-HostAction([string]$Kind, [string]$Target, [int]$Duration = 0) {
    $name = 'SockTuner-' + [guid]::NewGuid().ToString('N')
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($name, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
    $process = $null
    try {
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList '--host-action', $name -PassThru
        if (-not $pipe.WaitForConnectionAsync().Wait(15000)) { throw "Worker did not connect: $Kind" }
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
        $id = [guid]::NewGuid().ToString()
        $payload = @{ RequestId=$id; Kind=$Kind; Target=$Target; DurationSeconds=$Duration } | ConvertTo-Json -Compress
        if (-not $writer.WriteLineAsync([string]$payload).Wait(10000)) { throw "Worker did not read request: $Kind" }
        $read = $reader.ReadLineAsync()
        if (-not $read.Wait(65000) -or -not $read.Result) { throw "Missing worker response: $Kind" }
        $result = $read.Result | ConvertFrom-Json
        if ($result.RequestId -ne $id) { throw 'Response identity mismatch.' }
        return $result
    } finally {
        $pipe.Dispose()
        if ($process -and -not $process.WaitForExit(10000)) { $process.Kill() }
    }
}

$before = (& pnputil.exe /enum-drivers) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the driver store before testing.' }
$capture = Invoke-HostAction 'CaptureDpcIsr' '127.0.0.1' 5
if (-not $capture.Success) { throw "ETW capture failed: $($capture.Error)" }
if (@($capture.Capture.Windows).Count -ne 5) { throw 'Expected five concurrent windows.' }
if ($capture.Capture.Baseline.Received -ne 12) { throw 'Baseline replies missing.' }
foreach ($window in $capture.Capture.Windows) {
    if ($window.Latency.Received -ne 4) { throw 'Concurrent replies missing.' }
    foreach ($sample in $window.Latency.Samples) {
        if ([datetimeoffset]$sample.Timestamp -lt [datetimeoffset]$window.StartedAt -or
            [datetimeoffset]$sample.Timestamp -ge [datetimeoffset]$window.EndedAt) {
            throw 'Latency outside the ETW window.'
        }
    }
}
$delete = Invoke-HostAction 'DeleteDriverPackage' 'not-an-oem-package.inf'
if ($delete.Success -or $delete.Error -notlike 'Cleanup refused:*') { throw 'Invalid cleanup was not refused.' }
$device = Get-PnpDevice -Class System | Select-Object -First 1
if (-not $device) { throw 'No System-class device available for refusal test.' }
$disable = Invoke-HostAction 'DisableDevice' $device.InstanceId
if ($disable.Success -or $disable.Error -notlike 'Device disable refused:*') { throw 'Critical-class disable was not refused.' }
$after = (& pnputil.exe /enum-drivers) -join "`n"
if ($LASTEXITCODE -ne 0 -or $before -ne $after) { throw 'Driver-store verification failed.' }
[pscustomobject]@{
    Windows = @($capture.Capture.Windows).Count
    BaselineReplies = $capture.Capture.Baseline.Received
    ConcurrentReplies = ($capture.Capture.Windows | ForEach-Object { $_.Latency.Received } | Measure-Object -Sum).Sum
    DriverNames = @($capture.Drivers | ForEach-Object { $_.DriverName })
    InvalidCleanupRefused = -not $delete.Success
    CriticalDeviceDisableRefused = -not $disable.Success
    DriverStoreUnchanged = $before -eq $after
} | ConvertTo-Json

#requires -Version 7
<#
.SYNOPSIS
  Independently monitors NetDM.Host while a three-scenario benchmark runs.
.DESCRIPTION
  Run in a SEPARATE PowerShell process. A monotonic 100-ms loop records
  its own observed cadence, asynchronous /api/health responsiveness,
  and optionally a host process's liveness/total CPU time.

  This process does not share .NET ThreadPool, GC, simulation locks or
  event loop with NetDM.Host. Comparing the CSV to the exported suite
  JSON can distinguish an app-local pause from a simultaneous machine
  scheduling gap. This is diagnostics, not proof of OS-wide suspension.
.EXAMPLE
  pwsh -File scripts/observe-host.ps1 -DurationSeconds 420 -BaseUrl http://127.0.0.1:5087
.EXAMPLE
  pwsh -File scripts/observe-host.ps1 -DurationSeconds 420 -HostProcessId 1234 -OutputPath benchmarks/results/H-watch.csv
#>
[CmdletBinding()]
param(
    [ValidateRange(1,7200)][int]$DurationSeconds = 420,
    [ValidateRange(50,1000)][int]$PeriodMilliseconds = 100,
    [ValidateRange(1,30)][int]$HttpPeriodSeconds = 1,
    [ValidateRange(100,10000)][int]$HttpTimeoutMilliseconds = 800,
    [string]$BaseUrl = 'http://127.0.0.1:5087',
    [ValidateRange(0,2147483647)][int]$HostProcessId = 0,
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $root = Split-Path -Parent $PSScriptRoot
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss', $culture)
    $OutputPath = Join-Path $root "benchmarks/results/netdm-external-$stamp.csv"
}
$path = [System.IO.Path]::GetFullPath($OutputPath)
$folder = [System.IO.Path]::GetDirectoryName($path)
[System.IO.Directory]::CreateDirectory($folder) | Out-Null

$healthUrl = $BaseUrl.TrimEnd('/') + '/api/health'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMilliseconds($HttpTimeoutMilliseconds)
$writer = [System.IO.StreamWriter]::new(
    $path, $false, [System.Text.UTF8Encoding]::new($false))
$hostHandle = $null
if ($HostProcessId -gt 0) {
    $hostHandle = [System.Diagnostics.Process]::GetProcessById($HostProcessId)
}
$pendingHttp = $null
$httpStartMs = 0.0
$nextHttpMs = 0.0
$lastHttpResult = 'not-tested'
$lastHttpLatencyMs = -1.0
$nextProcessSampleMs = 0.0
$hostAlive = -1
$hostCpuSeconds = -1.0
$previousMs = 0.0
$nextTickMs = [double]$PeriodMilliseconds
$nextFlushMs = 1000.0
$samples = 0
$largeGaps = 0
$maxGapMs = 0.0
try {
    $writer.WriteLine('utc,elapsed_ms,gap_ms,extra_gap_ms,http_state,http_rtt_ms,http_pending_age_ms,host_alive,host_cpu_seconds')
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $nowMs = $clock.Elapsed.TotalMilliseconds
        if ($nowMs -lt $nextTickMs) {
            $sleep = [int][Math]::Floor($nextTickMs - $nowMs)
            if ($sleep -ge 1) {
                [System.Threading.Thread]::Sleep($sleep)
            } else {
                [System.Threading.Thread]::Yield() | Out-Null
            }
            continue
        }

        $gapMs = if ($samples -eq 0) { [double]$PeriodMilliseconds } else { $nowMs - $previousMs }
        $extraGapMs = [Math]::Max(0.0, $gapMs - $PeriodMilliseconds)
        $maxGapMs = [Math]::Max($maxGapMs, $gapMs)
        if ($extraGapMs -ge 250) { $largeGaps++ }
        $previousMs = $nowMs
        # Never replay missed samples; one row is one real observation.
        $nextTickMs = $nowMs + $PeriodMilliseconds

        $httpPendingAgeMs = -1.0
        if ($null -ne $pendingHttp -and $pendingHttp.IsCompleted) {
            $lastHttpLatencyMs = $nowMs - $httpStartMs
            try {
                $response = $pendingHttp.GetAwaiter().GetResult()
                $lastHttpResult = 'http-' + [int]$response.StatusCode
                $response.Dispose()
            } catch {
                $lastHttpResult = 'http-error'
            } finally {
                $pendingHttp = $null
            }
        }
        if ($null -ne $pendingHttp) {
            $httpPendingAgeMs = $nowMs - $httpStartMs
        }
        if ($null -eq $pendingHttp -and $nowMs -ge $nextHttpMs) {
            try {
                # Nonblocking: a dead/unresponsive host cannot freeze the watchdog.
                $pendingHttp = $client.GetAsync($healthUrl)
                $httpStartMs = $nowMs
                $lastHttpResult = 'http-pending'
                $lastHttpLatencyMs = -1
            } catch {
                $lastHttpResult = 'http-error'
                $pendingHttp = $null
            }
            $nextHttpMs = $nowMs + $HttpPeriodSeconds * 1000.0
        }
        if ($null -ne $pendingHttp) {
            $httpPendingAgeMs = $nowMs - $httpStartMs
        }

        if ($null -ne $hostHandle -and $nowMs -ge $nextProcessSampleMs) {
            try {
                $hostAlive = if ($hostHandle.HasExited) { 0 } else { 1 }
                $hostCpuSeconds = if ($hostAlive -eq 1) {
                    $hostHandle.TotalProcessorTime.TotalSeconds
                } else { -1.0 }
            } catch {
                $hostAlive = 0
                $hostCpuSeconds = -1.0
            }
            $nextProcessSampleMs = $nowMs + 1000.0
        }
        $utc = [DateTimeOffset]::UtcNow.ToString('O', $culture)
        $line = [string]::Format($culture,
            '{0},{1:F3},{2:F3},{3:F3},{4},{5:F3},{6:F3},{7},{8:F3}',
            $utc, $nowMs, $gapMs, $extraGapMs, $lastHttpResult,
            $lastHttpLatencyMs, $httpPendingAgeMs, $hostAlive, $hostCpuSeconds)
        $writer.WriteLine($line)
        $samples++

        if ($nowMs -ge $nextFlushMs) {
            $writer.Flush()
            $nextFlushMs = $nowMs + 1000.0
        }
    }
} finally {
    $writer.Flush()
    $writer.Dispose()
    $client.Dispose()
    if ($null -ne $hostHandle) { $hostHandle.Dispose() }
}
Write-Host ("External watchdog: {0} samples; {1} gaps >=250ms; max cadence gap {2:N1}ms; CSV: {3}" -f
    $samples, $largeGaps, $maxGapMs, $path)

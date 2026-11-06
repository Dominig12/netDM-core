#requires -Version 7
<#
.SYNOPSIS
  Captures GC EventPipe events and a separate-process host watchdog.
.DESCRIPTION
  Requires PowerShell 7 and dotnet-trace (dotnet tool install -g dotnet-trace).
  Start before H/I suite, run one mode at a time. Does not start suite.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$Label = 'H',
    [ValidateRange(1,7200)][int]$DurationSeconds = 420,
    [string]$BaseUrl = 'http://127.0.0.1:5087',
    [ValidateSet('gc-collect','gc-verbose')][string]$TraceProfile = 'gc-collect',
    [ValidateRange(0,2147483647)][int]$HostProcessId = 0,
    [string]$OutputDirectory = 'benchmarks/results',
    [switch]$SkipTrace
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$folder = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
$folder = [System.IO.Path]::GetFullPath($folder)
[System.IO.Directory]::CreateDirectory($folder) | Out-Null

if ($HostProcessId -eq 0) {
    $health = Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/api/health') -TimeoutSec 5
    if ($health.status -ne 'ok' -or [int]$health.processId -le 0) {
        throw 'Host must run an M18.5 build that reports /api/health.processId.'
    }
    $HostProcessId = [int]$health.processId
}
$target = [System.Diagnostics.Process]::GetProcessById($HostProcessId)
if ($target.HasExited) { throw "Target process $HostProcessId has already exited." }
$pwsh = Get-Command pwsh -ErrorAction Stop
$trace = $null
if (-not $SkipTrace) {
    $trace = Get-Command dotnet-trace -ErrorAction SilentlyContinue
    if ($null -eq $trace) {
        throw 'Install dotnet-trace first: dotnet tool install --global dotnet-trace'
    }
}
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$prefix = Join-Path $folder "netdm-$Label-$stamp"
$watchCsv = "$prefix-external.csv"
$watchLog = "$prefix-external.log"
$watchErrors = "$prefix-external-errors.log"
$nettrace = "$prefix.nettrace"
$manifestPath = "$prefix-manifest.json"
$watchdogScript = Join-Path $PSScriptRoot 'observe-host.ps1'
if (-not (Test-Path $watchdogScript)) { throw "Missing script: $watchdogScript" }
$watchDuration = [Math]::Min(7200, $DurationSeconds + 8)
# Start-Process joins ArgumentList into one command line. Paths may have spaces.
$watchdogArgs = @(
    '-NoProfile', '-File', ('"' + $watchdogScript + '"'),
    '-DurationSeconds', [string]$watchDuration,
    '-BaseUrl', $BaseUrl,
    '-HostProcessId', [string]$HostProcessId,
    '-OutputPath', ('"' + $watchCsv + '"')
)
$watchdog = $null
$started = [DateTimeOffset]::UtcNow
$traceExit = $null
try {
    $watchdog = Start-Process -FilePath $pwsh.Source -ArgumentList $watchdogArgs -PassThru -RedirectStandardOutput $watchLog -RedirectStandardError $watchErrors
    Write-Host "NetDM PID: $HostProcessId; watchdog CSV: $watchCsv"
    if (-not $SkipTrace) {
        $duration = [TimeSpan]::FromSeconds($DurationSeconds).ToString('dd\:hh\:mm\:ss')
        Write-Host "Recording $TraceProfile for $duration into $nettrace"
        & $trace.Source collect --process-id $HostProcessId --duration $duration --profile $TraceProfile --buffersize 64 --output $nettrace
        $traceExit = $LASTEXITCODE
        if ($traceExit -ne 0) { throw "dotnet-trace failed (exit code $traceExit)" }
    } else {
        Start-Sleep -Seconds $DurationSeconds
    }
} finally {
    if ($null -ne $watchdog) {
        if (-not $watchdog.WaitForExit(20000)) {
            try { $watchdog.Kill() } catch { }
            [void]$watchdog.WaitForExit(2000)
        }
    }
    [ordered]@{
        label = $Label
        targetProcessId = $HostProcessId
        targetProcessName = $target.ProcessName
        startedUtc = $started.ToString('O')
        finishedUtc = ([DateTimeOffset]::UtcNow).ToString('O')
        requestedSeconds = $DurationSeconds
        profile = if ($SkipTrace) { 'disabled (CI)' } else { $TraceProfile }
        traceExitCode = $traceExit
        nettrace = if ($SkipTrace) { $null } else { $nettrace }
        externalCsv = $watchCsv
        watchdogLog = $watchLog
        watchdogErrorLog = $watchErrors
    } | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 $manifestPath
    $target.Dispose()
    Write-Host "Capture manifest: $manifestPath"
}

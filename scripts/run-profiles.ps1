#requires -Version 7
[CmdletBinding()]
param(
    [string]$Sizes = '1000,10000,30000',
    [string]$Seeds = '1337,42,2026',
    [ValidateRange(10,1000)][int]$Routes = 160,
    [ValidateRange(10,1000)][int]$ViewportQueries = 120,
    [ValidateRange(0,250)][int]$MoveBatches = 32,
    [string]$OutputDirectory = 'benchmarks/results',
    [string]$BaseUrl = 'http://127.0.0.1:5087',
    [switch]$LaunchHost
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$counts = @($Sizes.Split(',') | ForEach-Object { [int]$_.Trim() })
$seedsParsed = @($Seeds.Split(',') | ForEach-Object { [int]$_.Trim() })
foreach ($n in $counts) {
    if ($n -lt 100 -or $n -gt 30000) { throw "Node count out of range: $n" }
}
$hostProcess = $null
$summary = [System.Collections.Generic.List[object]]::new()
try {
    if ($LaunchHost) {
        $stdout = Join-Path $destination 'host-stdout.log'
        $stderr = Join-Path $destination 'host-stderr.log'
        $hostProcess = Start-Process -FilePath dotnet -ArgumentList @('run','--project','src/NetDM.Host','-c','Release','--no-build') -WorkingDirectory $root -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    }
    $healthy = $false
    for ($i=0; $i -lt 80; $i++) {
        try {
            $health = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/health" -TimeoutSec 2
            if ($health.status -eq 'ok') { $healthy = $true; break }
        } catch {
            if ($hostProcess -and $hostProcess.HasExited) { throw "Host exited before ready. See host-stderr.log." }
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $healthy) { throw "No healthy netDM.Host at $BaseUrl" }
    foreach ($nodes in $counts) {
        foreach ($seed in $seedsParsed) {
            $label = "nodes-$nodes-seed-$seed"
            Write-Host "Profiling $label"
            $body = @{nodes=$nodes;routes=$Routes;viewportQueries=$ViewportQueries;moveBatches=$MoveBatches;seed=$seed} | ConvertTo-Json -Compress
            $accepted = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/comparison/start" -ContentType 'application/json' -Body $body -TimeoutSec 30
            if ($accepted.status -notin @('running','complete')) { throw "Comparison not accepted." }
            $deadline = (Get-Date).AddMinutes(15)
            do {
                Start-Sleep -Milliseconds 650
                $state = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/comparison" -TimeoutSec 30
                if ($state.status -in @('failed','stopped')) { throw "Comparison failed: $($state.error)" }
                if ((Get-Date) -gt $deadline) {
                    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/comparison/stop" | Out-Null
                    throw "Comparison timed out: $label"
                }
            } while ($state.status -ne 'complete')
            $report = $state.report
            $metadata = [ordered]@{
                capturedAt=(Get-Date).ToUniversalTime().ToString('o')
                environment=[ordered]@{
                    os=[System.Runtime.InteropServices.RuntimeInformation]::OSDescription
                    logicalProcessors=[System.Environment]::ProcessorCount
                    powershell=$PSVersionTable.PSVersion.ToString()
                }
                report=$report
            }
            $metadata | ConvertTo-Json -Depth 30 | Set-Content -Path (Join-Path $destination "$label.json") -Encoding utf8
            $baseline = $report.algorithms | Select-Object -First 1
            $best = $report.algorithms | Sort-Object p95Us | Select-Object -First 1
            $summary.Add([pscustomobject]@{
                nodes=$nodes
                seed=$seed
                fastestP95Algorithm=$best.name
                fastestP95Microseconds=[math]::Round($best.p95Us,2)
                baselineP95Microseconds=[math]::Round($baseline.p95Us,2)
                csrBuildMilliseconds=[math]::Round($report.csrBuildMs,2)
                spatialIndexP95Microseconds=[math]::Round($report.spatial.indexedP95Us,2)
                fullScanP95Microseconds=[math]::Round($report.spatial.scanP95Us,2)
                movementBatchP95Microseconds=[math]::Round($report.movement.batchP95Us,2)
            })
            Write-Host "Saved $label.json"
        }
    }
    $summary | Export-Csv -Path (Join-Path $destination 'summary.csv') -NoTypeInformation -Encoding utf8
    $summary | Format-Table -AutoSize | Out-Host
} finally {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

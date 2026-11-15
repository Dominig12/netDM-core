param(
    [string] $BuildDirectory = 'TestResults\p1\game-src',
    # The Host's address; by default it is read from the stand's Host log ("Now listening on: ...").
    [string] $BaseUrl,
    # Repeat every N seconds until Ctrl+C; 0 prints once.
    [int] $Every = 0
)
# What a running stand (play-bluemoon.ps1 or start-bluemoon-stand.ps1) reports over its loopback HTTP:
# health, readiness, the published map, research and the background stages. Read-only.
# Run from the netDM-core root in a second window:  pwsh -File scripts\stand-status.ps1 -Every 10
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
if (-not $BaseUrl) {
    $log = Join-Path $BuildDirectory 'netdm-stand-host.log'
    if (-not (Test-Path -LiteralPath $log)) { throw "No stand Host log at $log. Is the stand running?" }
    $line = Select-String -LiteralPath $log -Pattern 'Now listening on: (http://127\.0\.0\.1:\d+)' | Select-Object -Last 1
    if (-not $line) { throw "The Host has not reported its address in $log yet." }
    $BaseUrl = $line.Matches[0].Groups[1].Value
}
if ($BaseUrl -notmatch '^http://(127\.0\.0\.1|localhost|\[::1\])(:\d+)?$') { throw 'Only a loopback Host address is allowed.' }

function Show([string] $Path, [int] $Chars = 600) {
    try {
        $response = Invoke-WebRequest -Uri "$BaseUrl$Path" -TimeoutSec 5 -SkipHttpErrorCheck
        $text = $response.Content
        try { $text = $text | ConvertFrom-Json | ConvertTo-Json -Compress -Depth 6 } catch { }
        if ($text.Length -gt $Chars) { $text = $text.Substring(0, $Chars) + ' …' }
        Write-Host ("{0,-24} {1}  {2}" -f $Path, [int]$response.StatusCode, $text)
    }
    catch { Write-Host ("{0,-24} ERR  {1}" -f $Path, $_.Exception.Message) }
}

do {
    Write-Host ("== {0:HH:mm:ss} {1}" -f (Get-Date), $BaseUrl)
    Show '/api/health' 200
    Show '/api/readiness/ipc' 300
    Show '/api/readiness/game' 300
    Show '/api/readiness/network' 300
    Show '/api/game/summary'
    Show '/api/research/status'
    Show '/api/scheduler' 400
    if ($Every -gt 0) { Start-Sleep -Seconds $Every }
} while ($Every -gt 0)

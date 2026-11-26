param(
    # Where the hermetic world and the bridge already are, exactly as test-bluemoon-unit.ps1 expects.
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $BridgeDll,
    [string] $TestList = 'scripts/bluemoon-netdm-tests.txt',
    # How many times to run the whole list. The intersection of failures across runs is the signal.
    [int] $Rounds = 3,
    [string] $HostDll = 'src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll',
    [string] $DreamDaemon = (Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin/dd.exe'),
    [int] $TimeoutMinutes = 20
)
# Repeats the live unit suite and reports which tests fail in every round. One failing test in one
# round is a flake; a test that fails every time is a defect worth chasing. Mirrors the plan recorded
# in docs/netrun/status.md: repeat, intersect, then read the world state at the failure.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$suite = Join-Path $root 'scripts/test-bluemoon-unit.ps1'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$list = (Resolve-Path -LiteralPath $TestList).Path
$runs = @()
for ($round = 1; $round -le $Rounds; $round++) {
    Write-Host "== Round $round of $Rounds"
    $output = & pwsh -NoProfile -File $suite -BuildDirectory $build -BridgeDll $BridgeDll `
        -TestList $list -HostDll $HostDll -DreamDaemon $DreamDaemon -TimeoutMinutes $TimeoutMinutes 2>&1 |
        ForEach-Object { $_.ToString() }
    $failed = [ordered]@{}
    foreach ($line in $output) {
        if ($line -match 'RESULT (?<test>\S+) status=1') { $failed[$Matches['test']] = $true }
    }
    $clean = $output -match 'clean_run\.lk: True'
    $runs += [pscustomobject]@{
        Round = $round
        Clean = [bool]$clean
        Failed = @($failed.Keys)
    }
    $shown = $failed.Count ? ($failed.Keys -join ', ') : 'none'
    Write-Host ("   failed: {0}" -f $shown)
}

$all = @($runs.Failed | ForEach-Object { $_ } | Sort-Object -Unique)
$always = @($all | Where-Object { $test = $_; -not ($runs | Where-Object { $test -notin $_.Failed }) })
Write-Host ''
$seen = $all.Count ? ($all -join ', ') : 'none'
Write-Host ("Tests seen failing: {0}" -f $seen)
$alwaysShown = $always.Count ? ($always -join ', ') : 'none'
Write-Host ("Failed in every round ({0}): {1}" -f $always.Count, $alwaysShown)
if ($always.Count) { exit 1 }

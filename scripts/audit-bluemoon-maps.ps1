param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $BridgeDll,
    # Station maps of the BlueMoon rotation; boxstation_variants_rand is audited through its variants.
    [string[]] $Maps = @('boxstation', 'peacesyndicatestation', 'metastation', 'pubbystation', 'omegastation',
        'deltastation', 'festivestation', 'kilostation', 'smolstation', 'taustation', 'cogstation', 'layeniastation',
        'lambdastation', 'icemoonstation', 'tramstation', 'northstar', 'voidraptor', 'icebox', 'blueshift',
        'nebulastation', 'catwalkstation', 'wawastation', 'biodome', 'ouroboros'),
    [string] $OutDirectory = 'TestResults/map-audit',
    [int] $TimeoutMinutes = 30
)
# Boots each station map in a UNIT_TESTS build without LOWMEMORYMODE (build it with
# build-bluemoon-unit-dmb.ps1 -Defines CBT,CIBUILDING,UNIT_TEST_PROFILE_HERMETIC), runs the netDM map
# audit test with a live Host and collects netdm_map_audit.json per map plus a summary table.
# Mining levels and space ruins are switched off in a copy of each map config: the audit needs the station.
$ErrorActionPreference = 'Stop'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$out = [IO.Directory]::CreateDirectory($OutDirectory).FullName
$runner = Join-Path $PSScriptRoot 'test-bluemoon-unit.ps1'
$list = Join-Path $out 'map-audit-tests.txt'
Set-Content -LiteralPath $list -Value '/datum/unit_test/netdm_observe/area_map/map_audit'
$configs = [IO.Directory]::CreateDirectory((Join-Path $build 'data/netdm-audit')).FullName

foreach ($map in $Maps) {
    $source = Join-Path $build "_maps/$map.json"
    if (-not (Test-Path -LiteralPath $source)) { Write-Host "MAP_AUDIT $map missing config"; continue }
    $config = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json -AsHashtable
    $config.minetype = 'none'
    $config.space_ruin_levels = 0
    $config.space_empty_levels = 0
    $config.Remove('map_variants')
    Set-Content -LiteralPath (Join-Path $configs "$map.json") -Value ($config | ConvertTo-Json -Depth 8)
    $started = Get-Date
    $output = & pwsh -NoProfile -File $runner -BuildDirectory $build -BridgeDll $BridgeDll -TestList $list `
        -Map "data/netdm-audit/$map.json" -LogDirectory "netdm-audit-$map" -TimeoutMinutes $TimeoutMinutes 2>&1 | Out-String
    Set-Content -LiteralPath (Join-Path $out "$map.log") -Value $output
    $logs = [regex]::Match($output, 'Logs: (.+)').Groups[1].Value.Trim()
    $result = [regex]::Match($output, 'RESULT \S+ status=(\d+)').Groups[1].Value
    $report = Join-Path $logs 'netdm_map_audit.json'
    if ($logs -and (Test-Path -LiteralPath $report)) { Copy-Item -LiteralPath $report -Destination (Join-Path $out "$map.json") -Force }
    Write-Host ("MAP_AUDIT {0} status={1} report={2} {3:N0} s" -f $map, ($result ? $result : 'none'),
        (Test-Path -LiteralPath (Join-Path $out "$map.json")), ((Get-Date) - $started).TotalSeconds)
}

# Summary table of every collected report.
$rows = foreach ($file in Get-ChildItem -LiteralPath $out -Filter '*.json' | Sort-Object Name) {
    $r = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    $largest = if ($r.component_sizes.Count) { $r.component_sizes[0] } else { 0 }
    $island_tiles = ($r.islands | Measure-Object -Property tiles -Sum).Sum
    $stationWithout = @($r.without_apc | Where-Object { $_.station })
    '| {0} | {1} | {2} | {3} | {4} | {5} ({6}) | {7} | {8} ({9}) | {10} ({11}) | {12} | {13} | {14} |' -f $r.map,
        ($r.station_levels -join ','), $r.apcs, $r.zones, $r.links, $r.components, $largest, $r.isolated.Count,
        $r.islands.Count, [int]$island_tiles, $stationWithout.Count, $r.without_apc_tiles, $r.unexported.Count,
        $r.multiple_apcs.Count, $r.mismatched_apcs.Count
}
$header = @(
    '| Карта | Z станции | APC | Зоны | Рёбра | Компоненты (крупнейшая) | Изолированные APC | Острова (клетки) | Зоны без APC (клетки) | Невыгруженные части | Зоны с несколькими APC | APC вне своей зоны |',
    '|---|---|---|---|---|---|---|---|---|---|---|---|')
Set-Content -LiteralPath (Join-Path $out 'summary.md') -Value ($header + $rows)
Write-Host "MAP_AUDIT_SUMMARY $(Join-Path $out 'summary.md')"

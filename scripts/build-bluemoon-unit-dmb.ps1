param(
    [Parameter(Mandatory)] [string] $GameRepository,
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [string] $DreamMaker = (Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin/dm.exe'),
    # Default: the hermetic unit-test profile. ('CBT','LOWMEMORYMODE') gives a playable runtime station.
    [string[]] $Defines = @('CBT', 'CIBUILDING', 'LOWMEMORYMODE', 'UNIT_TEST_PROFILE_HERMETIC'),
    # Copies the built TGUI bundle (tgui/public) next to the DMB.
    [switch] $WithTgui
)
# Refreshes the netDM worktree files in a tree made by deploy/bluemoon/Export-Source.ps1 (same game
# commit) and compiles tgstation.m.dmb in place. The existing tgstation.rsc is renamed to
# tgstation.m.rsc and reused, so the build needs little extra disk space.
# Run it from a short path: DreamMaker hits MAX_PATH in deep directories. Then use test-bluemoon-unit.ps1.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $GameRepository).Path
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$files = @('tgstation.dme', 'code/game/world.dm', 'code/game/area/areas.dm', 'code/controllers/subsystem/netdm_observe.dm',
    'code/__DEFINES/netdm.dm', 'code/modules/netdm/netdm_bridge.dm', 'code/modules/netdm/apc_map.dm',
    'code/modules/netdm/netdm_node.dm', 'code/modules/netdm/topology_view.dm',
    'code/modules/netdm/camera_access.dm', 'code/game/machinery/computer/camera.dm', 'code/modules/atmospherics/machinery/airalarm.dm',
    'code/modules/netdm/netdm_services.dm', 'code/modules/netdm/network_diagnostics.dm', 'code/modules/netdm/network_terminal.dm', 'code/modules/netdm/netdm_service_lane.dm', 'code/modules/netdm/atmos_telemetry.dm', 'code/modules/netdm/research_shadow.dm', 'code/modules/netdm/messenger_shadow.dm', 'code/modules/netdm/messenger_relay.dm', 'code/modules/netdm/radio_shadow.dm', 'code/modules/netdm/radio_uplink.dm', 'code/modules/netdm/door_control_shadow.dm', 'code/modules/research/techweb/netdm_research_hooks.dm', 'code/modules/admin/admin_verbs.dm',
    'code/modules/buildmode/submodes/area_edit.dm', 'code/modules/lighting/lighting_turf.dm',
    'code/modules/power/apc.dm', 'code/modules/shuttle/docking.dm', 'code/controllers/subsystem/mapping.dm',
    'code/modules/modular_computers/computers/item/computer.dm', 'code/modules/modular_computers/computers/item/computer_ui.dm',
    'code/modules/research/techweb/nodes/robotics_nodes.dm',
    # ResearchEvidence call sites (14.11.2026): the points carry their source machine.
    'code/modules/research/techweb/_techweb.dm', 'code/modules/research/destructive_analyzer.dm', 'code/modules/research/experimentor.dm',
    'code/game/machinery/doppler_array.dm', 'code/modules/power/tesla/coil.dm', 'code/modules/power/singularity/collector.dm',
    'code/modules/surgery/experimental_dissection.dm', 'code/modules/netdm/network_space.dm',
    'code/__DEFINES/dcs/signals.dm', 'code/game/machinery/_machinery.dm',
    'code/modules/mapping/space_management/space_reservation.dm', 'code/modules/unit_tests/_unit_tests.dm',
    'code/modules/unit_tests/netdm_observe.dm')
# `pwsh -File` passes a list as one comma-separated string.
$Defines = @($Defines | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($file in $files) {
    $target = Join-Path $build $file
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $target -Force
}
if ($WithTgui) {
    foreach ($asset in 'tgui.html', 'tgui.bundle.js', 'tgui.bundle.css', 'tgui-panel.bundle.js', 'tgui-panel.bundle.css') {
        $target = Join-Path $build "tgui/public/$asset"
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath (Join-Path $repo "tgui/public/$asset") -Destination $target -Force
    }
}
if ($Defines -contains 'UNIT_TEST_PROFILE_HERMETIC') {
    # The hermetic unit-test profile loads CentCom_hermetic.dmm. The game's build.js generates it and git
    # ignores it, so a fresh checkout has none: then it is cut here the same way (the TGM map's z1 blocks).
    $hermetic = Join-Path $build '_maps/map_files/generic/CentCom_hermetic.dmm'
    $generated = Join-Path $repo '_maps/map_files/generic/CentCom_hermetic.dmm'
    if (Test-Path -LiteralPath $generated) {
        Copy-Item -LiteralPath $generated -Destination $hermetic -Force
    }
    else {
        $map = [IO.File]::ReadAllText((Join-Path $build '_maps/map_files/generic/CentCom.dmm'))
        $first = [regex]::Match($map, '(?m)^\(\d+,\d+,\d+\) = \{"')
        if (-not $first.Success) { throw 'CentCom.dmm is not in TGM format; cannot cut z1 out of it.' }
        $blocks = [regex]::Split($map.Substring($first.Index), '(?m)^(?=\(\d+,\d+,\d+\) = \{")')
        $kept = @($blocks | Where-Object { $_ -match '^\(\d+,\d+,1\) = \{"' })
        if ($kept.Count -eq 0) { throw 'CentCom.dmm has no z1 blocks.' }
        [IO.File]::WriteAllText($hermetic, $map.Substring(0, $first.Index) + ($kept -join ''))
    }
}
$header = ($Defines | ForEach-Object { "#define $_`n" }) -join ''
$dme = Join-Path $build 'tgstation.m.dme'
[IO.File]::WriteAllText($dme, $header + [IO.File]::ReadAllText((Join-Path $build 'tgstation.dme')))
$rsc = Join-Path $build 'tgstation.rsc'
$testRsc = Join-Path $build 'tgstation.m.rsc'
if ((Test-Path -LiteralPath $rsc) -and -not (Test-Path -LiteralPath $testRsc)) { Move-Item -LiteralPath $rsc -Destination $testRsc }
Push-Location $build
try {
    $output = & $DreamMaker 'tgstation.m.dme' 2>&1 | Out-String
    $code = $LASTEXITCODE
}
finally { Pop-Location }
$output -split "`n" | Where-Object { $_ -match 'error|warning|tgstation\.m\.dmb' } | Select-Object -Last 40
if ($code -ne 0 -or $output -notmatch ' - 0 errors, 0 warnings') { throw "DreamMaker failed with exit code $code." }
Write-Host "UNIT_DMB_READY defines=$($Defines -join ',')"

param(
    [Parameter(Mandatory)] [string] $GameRepository,
    [Parameter(Mandatory)] [string] $Destination
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $GameRepository).Path
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Source export requires a new directory.' }
$null = [IO.Directory]::CreateDirectory($target)
$archive = [IO.Path]::GetTempFileName()
try {
    git -C $repo archive --format=tar --output=$archive HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
    if ($env:OS -eq 'Windows_NT') {
        # Windows' own bsdtar: a GNU tar earlier in PATH (Git Bash) has no --options.
        & (Join-Path $env:SystemRoot 'System32\tar.exe') --options hdrcharset=UTF-8 -xf $archive -C $target
    }
    else {
        tar -xf $archive -C $target
    }
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
}
finally { [IO.File]::Delete($archive) }

# Worktree inputs for Observe and its map hooks. Configuration comes from HEAD.
$observeFiles = @('tgstation.dme', 'code/game/world.dm', 'code/game/area/areas.dm', 'code/controllers/subsystem/netdm_observe.dm',
    'code/__DEFINES/netdm.dm', 'code/modules/netdm/netdm_bridge.dm', 'code/modules/netdm/apc_map.dm',
    'code/modules/netdm/netdm_node.dm', 'code/modules/netdm/topology_view.dm',
    'code/modules/netdm/camera_access.dm', 'code/game/machinery/computer/camera.dm', 'code/modules/atmospherics/machinery/airalarm.dm',
    'code/modules/netdm/netdm_services.dm', 'code/modules/netdm/network_diagnostics.dm', 'code/modules/netdm/network_terminal.dm', 'code/modules/netdm/netdm_service_lane.dm', 'code/modules/netdm/atmos_telemetry.dm', 'code/modules/netdm/research_shadow.dm', 'code/modules/netdm/messenger_shadow.dm', 'code/modules/netdm/messenger_relay.dm', 'code/modules/netdm/radio_shadow.dm', 'code/modules/netdm/radio_uplink.dm', 'code/modules/netdm/door_control_shadow.dm', 'code/modules/research/techweb/netdm_research_hooks.dm', 'code/modules/admin/admin_verbs.dm',
    'tgui/packages/tgui/interfaces/NetdmTerminal.tsx', 'tgui/packages/tgui/interfaces/NtosMain.js',
    'tgui/packages/tgui/interfaces/NetdmSpace.tsx', 'tgui/packages/tgui/interfaces/CameraConsole.js',
    'code/modules/netdm/network_space.dm',
    'tgui/packages/tgui/package.json', 'tgui/yarn.lock', 'tgui/packages/tgui/interfaces/PaperSheet.test.tsx',
    'tgui/packages/tgui/interfaces/NetdmTopology/index.tsx', 'tgui/packages/tgui/interfaces/NetdmTopology/model.ts',
    'tgui/packages/tgui/interfaces/NetdmTopology/scene.ts', 'tgui/packages/tgui/interfaces/NetdmTopology/model.test.ts',
    'tgui/packages/tgui/interfaces/NetdmTopology/index.test.tsx',
    'code/modules/buildmode/submodes/area_edit.dm', 'code/modules/lighting/lighting_turf.dm',
    'code/modules/power/apc.dm', 'code/modules/shuttle/docking.dm',
    'code/modules/modular_computers/computers/item/computer.dm', 'code/modules/modular_computers/computers/item/computer_ui.dm',
    'code/modules/research/techweb/nodes/robotics_nodes.dm',
    'code/__DEFINES/dcs/signals.dm', 'code/game/machinery/_machinery.dm',
    'code/controllers/subsystem/mapping.dm', 'code/modules/mapping/space_management/space_reservation.dm',
    'code/modules/unit_tests/_unit_tests.dm', 'code/modules/unit_tests/netdm_observe.dm')
$sourceFiles = foreach ($file in $observeFiles) {
    $source = Join-Path $repo $file
    $output = Join-Path $target $file
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    Copy-Item -LiteralPath $source -Destination $output
    [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant() }
}
[ordered]@{ commit = (git -C $repo rev-parse HEAD); observeSources = @($sourceFiles) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'netdm-source.json') -Encoding utf8
# The legacy game's Dockerfile excludes SQL. This export already contains only release inputs.
[IO.File]::WriteAllText((Join-Path $target '.dockerignore'), ".git`nnode_modules`ntgui/node_modules`n")

param(
    # host: Host build, smoke tests, IPC test (a few minutes, no BYOND needed).
    # world: hermetic BlueMoon DMB and the netDM DreamDaemon tests with a live Host (about 15 minutes).
    # tgui: ESLint, TypeScript, Jest and the TGUI bundle of the game.
    # load: a short P10 load run (about two minutes).
    # bridge: the BYOND bridge smoke world (BYOND and the built bridge DLL).
    # Several groups: -Only tgui,world (pwsh -File passes the list as one string; it is split below).
    [string[]] $Only = @('host'),
    [string] $GameRepository = '..\MOLOT-BlueMoon-Station',
    [string] $BuildDirectory = 'TestResults\p1\game-src',
    [string] $BridgeDll = 'TestResults\p3-bridge\netdm_byond.dll',
    [string] $TestList = 'scripts\bluemoon-netdm-tests.txt'
)
# Runs the chosen groups of automated checks one after another, keeps going after a failure and prints one
# table at the end. Exit code 1 when any check failed. Run from anywhere:  pwsh -File scripts\check-all.ps1 -Only all
# The world group rebuilds the DMB in -BuildDirectory with the unit-test profile: start play-bluemoon.ps1 without
# -NoBuild afterwards.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$groups = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$known = @('host', 'world', 'tgui', 'browser', 'load', 'bridge', 'all')
$unknown = @($groups | Where-Object { $_ -notin $known })
if ($unknown.Count) { throw "Unknown group: $($unknown -join ', '). Use $($known -join ', ')." }
if ($groups -contains 'all') { $groups = @('host', 'tgui', 'browser', 'world', 'load', 'bridge') }
$results = [Collections.Generic.List[object]]::new()
# Absolute: the TGUI checks run from the game's tgui folder.
$logs = (New-Item -ItemType Directory -Force -Path (Join-Path 'TestResults' ("check-all-{0:yyyyMMdd-HHmmss}" -f (Get-Date)))).FullName

# One check: its output goes to a log file, its last lines to the screen when it fails.
function Invoke-Check([string] $Name, [scriptblock] $Body, [scriptblock] $Passed = { $LASTEXITCODE -eq 0 }) {
    Write-Host "== $Name"
    $log = Join-Path $logs (($Name -replace '[^A-Za-z0-9]+', '-').Trim('-') + '.log')
    $started = Get-Date
    $ok = $false
    try {
        $global:LASTEXITCODE = 0
        $output = @(& $Body 2>&1 | ForEach-Object { "$_" })
        Set-Content -LiteralPath $log -Value $output
        $ok = [bool](& $Passed $output)
    }
    catch {
        $output = @("$_")
        try { Add-Content -LiteralPath $log -Value "$_" } catch { }
    }
    if (-not $ok) { $output | Select-Object -Last 25 | ForEach-Object { Write-Host "   $_" } }
    $seconds = [int]((Get-Date) - $started).TotalSeconds
    Write-Host ("   {0} in {1} s, log {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $seconds, $log)
    $results.Add([pscustomobject]@{ Check = $Name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = $seconds })
}

function Get-Yarn([string] $Tgui) {
    $yarn = Get-ChildItem -LiteralPath (Join-Path $Tgui '.yarn/releases') -Filter 'yarn-*.cjs' | Select-Object -First 1
    if (!$yarn) { throw 'The pinned Yarn release is missing in tgui/.yarn/releases.' }
    $yarn.FullName
}

if ($groups -contains 'host' -or $groups -contains 'world' -or $groups -contains 'load' -or $groups -contains 'bridge') {
    Invoke-Check 'Host build' { dotnet build src/NetDM.Host -c Release -nologo -v quiet }
}
if ($groups -contains 'host') {
    Invoke-Check 'Smoke tests' { dotnet run --project tests/NetDM.SmokeTests -c Release }
    Invoke-Check 'Host IPC' { pwsh -NoProfile -File tests/host-ipc.ps1 }
}
if ($groups -contains 'tgui') {
    $tgui = Join-Path $GameRepository 'tgui'
    . "$PSScriptRoot\tgui-tools.ps1"
    Invoke-Check 'TGUI packages' { Assert-NodeForTgui; Install-TguiPackages $GameRepository }
    $files = @('packages/tgui/interfaces/NetdmTerminal.tsx', 'packages/tgui/interfaces/NetdmSpace.tsx',
        'packages/tgui/interfaces/NetdmTopology', 'packages/tgui/interfaces/CameraConsole.js', 'packages/tgui/interfaces/NtosMain.js')
    Push-Location $tgui
    try {
        $yarn = Get-Yarn '.'
        Invoke-Check 'TGUI ESLint' { node $yarn eslint --ext .js,.ts,.tsx @files }
        Invoke-Check 'TGUI TypeScript' { node $yarn tsc --noEmit }
        Invoke-Check 'TGUI Jest' { node $yarn jest packages/tgui/interfaces/NetdmTopology }
        Invoke-Check 'TGUI bundle' { node $yarn run tgui:build }
    }
    finally { Pop-Location }
}
if ($groups -contains 'browser') {
    Invoke-Check 'Scene in real browser' { pwsh -NoProfile -File scripts/test-netdm-scene-browser.ps1 -GameRepository $GameRepository }
}
if ($groups -contains 'world') {
    Invoke-Check 'BlueMoon unit DMB' { pwsh -NoProfile -File scripts/build-bluemoon-unit-dmb.ps1 -GameRepository $GameRepository -BuildDirectory $BuildDirectory } `
        { param($out) $LASTEXITCODE -eq 0 -and ($out -match 'UNIT_DMB_READY') }
    # Cameras in authoritative mode: otherwise network_cameras passes without checking anything.
    Invoke-Check 'BlueMoon netDM tests' {
        pwsh -NoProfile -File scripts/test-bluemoon-unit.ps1 -BuildDirectory $BuildDirectory -BridgeDll $BridgeDll -TestList $TestList -NetworkCameras
    } {
        param($out)
        $rows = @($out -match '^RESULT ')
        $rows | ForEach-Object { Write-Host "   $_" }
        $rows.Count -gt 0 -and -not ($rows -match ' status=1 ') -and ($out -match 'clean_run.lk: True')
    }
}
if ($groups -contains 'load') {
    Invoke-Check 'P10 load (short)' { pwsh -NoProfile -File tests/p10-load.ps1 -Apcs 200 -Seconds 10 } `
        { param($out) $out -match 'NETDM_P10_LOAD_DONE' }
}
if ($groups -contains 'bridge') {
    Invoke-Check 'BYOND bridge world' { pwsh -NoProfile -File scripts/test-byond-world.ps1 -BridgeDll $BridgeDll }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Logs: $logs"
if ($results | Where-Object Result -eq 'FAIL') { exit 1 }

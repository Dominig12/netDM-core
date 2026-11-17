param(
    [Parameter(Mandatory)] [string] $GameSource,
    [Parameter(Mandatory)] [string] $GameBuild,
    [Parameter(Mandatory)] [string] $HostPublish,
    [Parameter(Mandatory)] [string] $BridgeDll,
    [Parameter(Mandatory)] [string] $ByondBin,
    [Parameter(Mandatory)] [string] $Destination,
    [string] $VcRuntimeBin = (Join-Path $env:WINDIR 'SysWOW64'),
    [string] $VcRuntime64Bin = (Join-Path $env:WINDIR 'System32'),
    [string] $MariaDbZip
)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Bundle destination must be new.' }
$source = (Resolve-Path -LiteralPath $GameSource).Path
$build = (Resolve-Path -LiteralPath $GameBuild).Path
$game = Join-Path $destinationPath 'game'
$byond = Join-Path $destinationPath 'runtime/byond/bin'
$sql = Join-Path $destinationPath 'runtime/sql'
$db = Join-Path $destinationPath 'runtime/mariadb'
foreach ($path in @($game, $byond, $sql, $db)) { $null = [IO.Directory]::CreateDirectory($path) }

function Copy-GameFile([string] $Root, [string] $RelativePath) {
    $target = Join-Path $game $RelativePath
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath (Join-Path $Root $RelativePath) -Destination $target
}

foreach ($dir in @('_maps', 'strings', 'config', 'icons/runtime')) {
    $target = Join-Path $game $dir
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath (Join-Path $source $dir) -Destination $target -Recurse
}
foreach ($dir in Get-ChildItem -LiteralPath $source -Directory) {
    if ($dir.Name -in @('icons', 'code', 'goon', 'citadel') -or $dir.Name.StartsWith('modular_')) {
        foreach ($icon in [IO.Directory]::EnumerateFiles($dir.FullName, '*.dmi', [IO.SearchOption]::AllDirectories)) {
            Copy-GameFile $source ([IO.Path]::GetRelativePath($source, $icon))
        }
    }
}
foreach ($file in @('tgstation.dmb', 'tgstation.rsc', 'tgui/public/tgui.html',
        'tgui/public/tgui.bundle.css', 'tgui/public/tgui.bundle.js', 'tgui/public/tgui-panel.bundle.css',
        'tgui/public/tgui-panel.bundle.js', 'tgui/packages/tgfont/dist/tgfont.css',
        'tgui/packages/tgfont/dist/tgfont.eot', 'tgui/packages/tgfont/dist/tgfont.woff2')) {
    Copy-GameFile $build $file
}
foreach ($file in @('rust_g.dll', 'auxmos.dll', 'libmariadb.dll', 'prof.dll', 'tools/loadout_crypto/crypto.cjs')) {
    Copy-GameFile $source $file
}
foreach ($file in @('LICENSE', 'GPLv3.txt')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destinationPath
}
Copy-Item -LiteralPath $BridgeDll -Destination (Join-Path $game 'netdm_byond.dll')
$node = (Get-Content -Raw -LiteralPath (Join-Path $build 'tools/loadout_crypto/node_path.txt')).Trim()
if ((& $node --version) -ne 'v24.16.0') { throw 'Expected Node 24.16.0 from the BlueMoon build.' }
Copy-Item -LiteralPath $node -Destination (Join-Path $game 'tools/loadout_crypto/node.exe')
[IO.File]::WriteAllText((Join-Path $game 'tools/loadout_crypto/node_path.txt'), 'tools/loadout_crypto/node.exe')

Copy-Item -Path (Join-Path $ByondBin '*') -Destination $byond -Recurse
$daemon = Join-Path $byond 'dd.exe'
if ((Get-Item -LiteralPath $daemon).VersionInfo.FileVersion -ne '5.0.516.1688') {
    throw 'Expected DreamDaemon 516.1688.'
}
foreach ($dll in @('msvcp140.dll', 'vcruntime140.dll')) {
    $runtime = Join-Path $VcRuntimeBin $dll
    Copy-Item -LiteralPath $runtime -Destination $byond
    Copy-Item -LiteralPath $runtime -Destination $game
}
Copy-Item -LiteralPath $HostPublish -Destination (Join-Path $destinationPath 'host') -Recurse
if (!(Test-Path -LiteralPath (Join-Path $destinationPath 'host/NetDM.Host.exe'))) { throw 'Missing Windows apphost.' }

foreach ($name in @('tgstation_schema.sql', 'bluemoon_schema.sql', 'bluemoon_whitelist_schema.sql', 'mentor.sql')) {
    Copy-Item -LiteralPath (Join-Path $source "SQL/$name") -Destination $sql
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'schema-revision.sql') -Destination $sql
$dbArchive = $MariaDbZip
if (!$dbArchive) {
    $dbArchive = Join-Path ([IO.Path]::GetTempPath()) 'mariadb-11.4.13-winx64.zip'
    if (!(Test-Path -LiteralPath $dbArchive)) {
        Invoke-WebRequest 'https://archive.mariadb.org/mariadb-11.4.13/winx64-packages/mariadb-11.4.13-winx64.zip' -OutFile $dbArchive
    }
}
if ((Get-FileHash -LiteralPath $dbArchive).Hash -ne 'd62986d433eeebfde218560b276103831604a61e929e87f1a17f5aebd80257e2') {
    throw 'MariaDB Windows ZIP SHA-256 mismatch.'
}
Expand-Archive -LiteralPath $dbArchive -DestinationPath $db
$mariaBin = Join-Path $db 'mariadb-11.4.13-winx64/bin'
foreach ($dll in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $runtime = Join-Path $VcRuntime64Bin $dll
    Copy-Item -LiteralPath $runtime -Destination $mariaBin
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $game 'tools/loadout_crypto')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-WindowsBundle.ps1') -Destination (Join-Path $destinationPath 'Start-BlueMoon.ps1')

$artifacts = foreach ($relative in @('game/tgstation.dmb', 'game/tgstation.rsc', 'game/netdm_byond.dll',
        'game/rust_g.dll', 'game/tools/loadout_crypto/node.exe', 'runtime/byond/bin/dd.exe',
        'runtime/byond/bin/byondcore.dll', 'runtime/mariadb/mariadb-11.4.13-winx64/bin/mariadbd.exe',
        'host/NetDM.Host.exe', 'host/NetDM.Host.dll', 'runtime/byond/bin/msvcp140.dll',
        'runtime/mariadb/mariadb-11.4.13-winx64/bin/vcruntime140_1.dll')) {
    [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath (Join-Path $destinationPath $relative)).Hash.ToLowerInvariant() }
}
$hostRuntime = (Get-Content -Raw -LiteralPath (Join-Path $destinationPath 'host/NetDM.Host.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions.includedFrameworks
if (!$hostRuntime) { throw 'Windows Host publish must be self-contained.' }
[ordered]@{
    platform = 'windows-x64-native'
    game = (Get-Content -Raw -LiteralPath (Join-Path $source 'netdm-source.json') | ConvertFrom-Json)
    coreCommit = (git -C $PSScriptRoot rev-parse HEAD)
    coreTreeDirty = [bool](git -C $PSScriptRoot status --porcelain)
    protocol = '1.1'; byond = '516.1688'; mariadb = '11.4.13'; rustG = '6.2.0'; node = '24.16.0'
    hostRuntime = $hostRuntime; artifacts = @($artifacts)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destinationPath 'release-manifest.json') -Encoding utf8
Write-Host "Windows bundle: $destinationPath"

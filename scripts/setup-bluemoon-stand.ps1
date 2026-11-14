param(
    [string] $GameRepository = '..\MOLOT-BlueMoon-Station',
    [string] $BuildDirectory = 'TestResults\p1\game-src',
    [string] $BridgeDirectory = 'TestResults\p3-bridge',
    # Rebuild the bridge and install TGUI packages even if they are there; a new export needs a new directory.
    [switch] $Rebuild
)
# Prepares a Windows machine for scripts\play-bluemoon.ps1, once per checkout:
#   1. checks the tools (Git, .NET 10 SDK, Node 24, CMake with the Visual Studio C++ toolchain, BYOND 516);
#   2. builds the Win32 call_ext bridge (netdm_byond.dll) with the local Visual Studio;
#   3. exports the BlueMoon sources into the build directory;
#   4. installs TGUI's packages in the BlueMoon checkout.
# Run from the netDM-core root:  pwsh -File scripts\setup-bluemoon-stand.ps1
# Keep both checkouts on a short path (for example C:\netdm): DreamMaker fails on long paths.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)

function Require([string] $Command, [string] $Hint) {
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) { throw "Not found: $Command. $Hint" }
}
Require git 'Install Git.'
Require dotnet 'Install the .NET 10 SDK.'
Require node 'Install Node.js 24.'
Require cmake 'Install CMake 3.21+ (the Visual Studio workload "Desktop development with C++" includes it).'
$dotnetMajor = [int](((dotnet --version) -split '\.')[0])
if ($dotnetMajor -lt 10) { throw "The .NET 10 SDK is required; dotnet --version is $(dotnet --version)." }
$dreamDaemon = Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin/dd.exe'
if (-not (Test-Path -LiteralPath $dreamDaemon)) { throw "BYOND 516 is not installed: $dreamDaemon" }
if (-not (Test-Path -LiteralPath (Join-Path $GameRepository 'tgstation.dme'))) {
    throw "BlueMoon checkout not found at $GameRepository (branch net-feature-dmcore-new)."
}

Write-Host '== Win32 bridge (netdm_byond.dll)'
$bridge = Join-Path $BridgeDirectory 'netdm_byond.dll'
if ($Rebuild -or -not (Test-Path -LiteralPath $bridge)) {
    # The default CMake generator on Windows is the newest installed Visual Studio; -A Win32 makes the
    # 32-bit DLL that 32-bit DreamDaemon loads.
    cmake -S native/byond -B native/byond/build -A Win32
    if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed: is the Visual Studio C++ toolchain installed?' }
    cmake --build native/byond/build --config Release
    if ($LASTEXITCODE -ne 0) { throw 'The bridge build failed.' }
    $null = New-Item -ItemType Directory -Force -Path $BridgeDirectory
    Copy-Item -LiteralPath 'native/byond/build/Release/netdm_byond.dll' -Destination $bridge -Force
}
Write-Host "   $bridge"

Write-Host '== BlueMoon sources'
if (Test-Path -LiteralPath $BuildDirectory) {
    Write-Host "   $BuildDirectory exists; play-bluemoon.ps1 refreshes the netDM files in it."
}
else {
    pwsh -NoProfile -File deploy/bluemoon/Export-Source.ps1 -GameRepository $GameRepository -Destination $BuildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The source export failed.' }
}

Write-Host '== TGUI packages'
Push-Location (Join-Path $GameRepository 'tgui')
try {
    if ($Rebuild -or -not (Test-Path -LiteralPath '.pnp.cjs')) {
        $yarn = Get-ChildItem -LiteralPath '.yarn/releases' -Filter 'yarn-*.cjs' | Select-Object -First 1
        if (!$yarn) { throw 'The pinned Yarn release is missing in tgui/.yarn/releases.' }
        node $yarn.FullName install
        if ($LASTEXITCODE -ne 0) { throw 'yarn install failed.' }
    }
}
finally { Pop-Location }

Write-Host @"

== Ready. Start a stand (builds the Host, TGUI and the world, then runs them):
   pwsh -File scripts\play-bluemoon.ps1 -Map metastation -NetworkCameras
"@

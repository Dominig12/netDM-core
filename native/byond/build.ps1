<#
.SYNOPSIS
    Builds the 32-bit BYOND call_ext bridge without Visual Studio, Docker or a system compiler.
.DESCRIPTION
    Downloads a pinned 32-bit MinGW-w64 toolchain once into native/byond/toolchain,
    configures the CMake project and builds netdm_byond.dll together with the
    32-bit netdm_bridge_test.exe. Then it proves the result: i386 PE machine,
    the six call_ext exports and no dependency on MinGW runtime DLLs.
    CMake is taken from PATH when present; otherwise a pinned portable copy is
    downloaded into the same toolchain folder.
.EXAMPLE
    pwsh -File native/byond/build.ps1
.EXAMPLE
    pwsh -File native/byond/build.ps1 -Configuration Debug -Clean
.EXAMPLE
    pwsh -File native/byond/build.ps1 -Destination TestResults/p3-bridge
.EXAMPLE
    # Air-gapped machine: put the pinned archive into toolchain/downloads first.
    pwsh -File native/byond/build.ps1 -Offline
#>
[CmdletBinding()]
param(
    # Directory that receives netdm_byond.dll and netdm_bridge_test.exe.
    [string] $Destination = (Join-Path $PSScriptRoot 'out'),
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string] $Configuration = 'Release',
    # Portable compiler and CMake live here. Git ignores this folder.
    [string] $ToolchainRoot = (Join-Path $PSScriptRoot 'toolchain'),
    # CMake binary directory. Default: native/byond/build.
    [string] $BuildDirectory = (Join-Path $PSScriptRoot 'build'),
    # Use this CMake executable instead of PATH or the portable copy.
    [string] $CMake,
    # Use this archive instead of downloading the pinned MinGW-w64 one.
    [string] $ToolchainArchive,
    [switch] $RefreshToolchain,
    [switch] $Clean,
    [switch] $Offline,
    [switch] $NoVerify
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'The Win32 BYOND bridge builds on Windows. On other hosts use scripts/build-bridge-mingw.ps1 (Docker cross-build).'
}

# Pinned toolchain: 32-bit GCC with POSIX threads and the MSVCRT runtime. POSIX
# threads keep std::thread/std::mutex identical to the verified MinGW build; the
# MSVCRT runtime avoids a UCRT dependency in the DreamDaemon process.
$MinGw = @{
    Version = 'GCC 16.2.0 + MinGW-w64 14.0.0 (i686, posix threads, msvcrt)'
    Archive = 'winlibs-i686-posix-dwarf-gcc-16.2.0-mingw-w64msvcrt-14.0.0-r2.zip'
    Url     = 'https://github.com/brechtsanders/winlibs_mingw/releases/download/16.2.0posix-14.0.0-msvcrt-r2/winlibs-i686-posix-dwarf-gcc-16.2.0-mingw-w64msvcrt-14.0.0-r2.zip'
    Sha256  = 'f1fab67fbe6ed1b0a390d06db6af8b4f0ca25fceab4f51bac2a88af8b81a93ec'
}
$PortableCMake = @{
    Archive = 'cmake-4.4.5-windows-x86_64.zip'
    Url     = 'https://github.com/Kitware/CMake/releases/download/v4.4.5/cmake-4.4.5-windows-x86_64.zip'
    Sha256  = '326c826486d46a23cf9d5a1c9ee3a567d427bdd02c4a34071937a2a675cff004'
}
$exports = @('netdm_init', 'netdm_send', 'netdm_poll', 'netdm_status', 'netdm_reconnect', 'netdm_shutdown')

function Write-Step([string] $Message) {
    Write-Host "== $Message"
}

function Save-File([string] $Url, [string] $Path, [string] $Sha256) {
    $expected = $Sha256.ToLowerInvariant()
    if (Test-Path -LiteralPath $Path) {
        $current = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($current -eq $expected) { return }
        Write-Host "   re-downloading $Path (sha256 $current)"
        Remove-Item -LiteralPath $Path -Force
    }
    if ($Offline) {
        throw "Offline mode: '$Path' is missing or does not match the pinned SHA-256. Place the archive there or drop -Offline."
    }
    $partial = "$Path.part"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Path))
    Write-Host "   downloading $([IO.Path]::GetFileName($Path))"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source --location --fail --silent --show-error --retry 3 --retry-delay 5 --connect-timeout 30 --output $partial $Url
        $code = $LASTEXITCODE
    }
    else {
        try { Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing; $code = 0 }
        catch { Write-Host "   $_"; $code = 1 }
    }
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $partial)) { throw "Download failed ($code): $Url" }
    $actual = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $partial -Force
        throw "SHA-256 mismatch for $Url`: expected $expected, got $actual."
    }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}

# Expand-Archive is slow on the compiler archive; Windows tar (bsdtar) reads zip.
function Expand-Zip([string] $Archive, [string] $Target) {
    $null = New-Item -ItemType Directory -Force -Path $Target
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($tar) {
        & $tar.Source -xf $Archive -C $Target
        if ($LASTEXITCODE -eq 0) { return }
        Write-Host "   tar exited with $LASTEXITCODE; retrying with Expand-Archive."
    }
    Expand-Archive -LiteralPath $Archive -DestinationPath $Target -Force
}

function Find-First([string] $Root, [string] $Filter) {
    Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Install-MinGw {
    $bin = Join-Path $ToolchainRoot 'mingw32\bin'
    $gxx = Join-Path $bin 'i686-w64-mingw32-g++.exe'
    if ((Test-Path -LiteralPath $gxx) -and -not $RefreshToolchain) { return $bin }

    Write-Step "Portable MinGW-w64 ($($MinGw.Version))"
    $archive = if ($ToolchainArchive) {
        (Resolve-Path -LiteralPath $ToolchainArchive).Path
    }
    else {
        Join-Path $ToolchainRoot ('downloads\' + $MinGw.Archive)
    }
    if ($ToolchainArchive) {
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $MinGw.Sha256) {
            throw "'$archive' is not the pinned toolchain: expected SHA-256 $($MinGw.Sha256), got $actual."
        }
    }
    else {
        Save-File $MinGw.Url $archive $MinGw.Sha256
    }

    if (Test-Path -LiteralPath (Join-Path $ToolchainRoot 'mingw32')) {
        Remove-Item -LiteralPath (Join-Path $ToolchainRoot 'mingw32') -Recurse -Force
    }
    $temp = Join-Path $ToolchainRoot ([IO.Path]::GetRandomFileName())
    try {
        Write-Host '   extracting the compiler'
        Expand-Zip $archive $temp
        $found = Find-First $temp 'i686-w64-mingw32-g++.exe'
        if (-not $found) { throw "'$archive' contains no i686-w64-mingw32-g++.exe." }
        $root = Split-Path -LiteralPath $found.DirectoryName
        Move-Item -LiteralPath $root -Destination (Join-Path $ToolchainRoot 'mingw32')
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $gxx)) { throw "The toolchain did not extract i686-w64-mingw32-g++.exe into '$bin'." }
    $machine = (& $gxx '-dumpmachine' 2>&1 | Out-String).Trim()
    if ($machine -notlike 'i686-*') { throw "The toolchain targets '$machine', not i686." }
    return $bin
}

function Install-PortableCMake {
    $exe = Join-Path $ToolchainRoot 'cmake\bin\cmake.exe'
    if (Test-Path -LiteralPath $exe) { return $exe }

    Write-Step 'Portable CMake'
    $archive = Join-Path $ToolchainRoot ('downloads\' + $PortableCMake.Archive)
    Save-File $PortableCMake.Url $archive $PortableCMake.Sha256
    if (Test-Path -LiteralPath (Join-Path $ToolchainRoot 'cmake')) {
        Remove-Item -LiteralPath (Join-Path $ToolchainRoot 'cmake') -Recurse -Force
    }
    $temp = Join-Path $ToolchainRoot ([IO.Path]::GetRandomFileName())
    try {
        Expand-Zip $archive $temp
        $found = Find-First $temp 'cmake.exe'
        if (-not $found) { throw "'$archive' contains no cmake.exe." }
        Move-Item -LiteralPath (Split-Path -LiteralPath $found.DirectoryName) -Destination (Join-Path $ToolchainRoot 'cmake')
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $exe)) { throw "The portable CMake did not extract into '$exe'." }
    return $exe
}

function Resolve-CMake {
    if ($CMake) {
        if (-not (Test-Path -LiteralPath $CMake)) { throw "CMake not found at '$CMake'." }
        return (Resolve-Path -LiteralPath $CMake).Path
    }
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    Install-PortableCMake
}

function Find-Artifact([string] $Name) {
    $candidates = @(
        (Join-Path $BuildDirectory $Name),
        (Join-Path (Join-Path $BuildDirectory $Configuration) $Name)
    )
    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $found) { throw "The build produced no $Name." }
    return $found
}

function Assert-I386PE([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x100 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw "'$Path' is not a PE image." }
    $offset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($offset -lt 0x40 -or ($offset + 6) -gt $bytes.Length) { throw "'$Path' has an invalid PE header offset." }
    if ($bytes[$offset] -ne 0x50 -or $bytes[$offset + 1] -ne 0x45) { throw "'$Path' has no PE signature." }
    $machine = [BitConverter]::ToUInt16($bytes, $offset + 4)
    if ($machine -ne 0x014C) { throw ("'{0}' is not i386: PE machine 0x{1:X4}." -f $Path, $machine) }
}

function Assert-BridgeExports([string] $Path, [string] $ObjDump) {
    $dump = (& $ObjDump '-p' $Path 2>&1 | Out-String)
    # GNU objdump prints "	[   0] +base[   1]  0000 netdm_init"; the wording differs
    # between binutils versions, so search the name table itself.
    $marker = '[Ordinal/Name Pointer] Table'
    $index = $dump.IndexOf($marker)
    if ($index -lt 0) { throw "'$Path' has no export name table." }
    $names = $dump.Substring($index)
    foreach ($name in $exports) {
        if ($names -notmatch "(?m)\b$name\s*$") { throw "'$Path' does not export $name." }
    }
    $imports = @([regex]::Matches($dump, '(?m)^\s*DLL Name: (\S+)') | ForEach-Object { $_.Groups[1].Value })
    $runtime = @($imports | Where-Object { $_ -match '^(libstdc\+\+|libgcc|libwinpthread|libssp)' })
    if ($runtime.Count) { throw "'$Path' depends on MinGW runtime DLLs: $($runtime -join ', ')." }
    if ($imports -notcontains 'KERNEL32.dll') { throw "'$Path' does not import KERNEL32.dll." }
    return $imports
}

$bin = Install-MinGw
$gxx = Join-Path $bin 'i686-w64-mingw32-g++.exe'
$make = Join-Path $bin 'mingw32-make.exe'
if (-not (Test-Path -LiteralPath $make)) { $make = Join-Path $bin 'make.exe' }
if (-not (Test-Path -LiteralPath $make)) { throw "The toolchain has no make in '$bin'." }
$objdump = Join-Path $bin 'i686-w64-mingw32-objdump.exe'
if (-not (Test-Path -LiteralPath $objdump)) { $objdump = Join-Path $bin 'objdump.exe' }
if (-not $NoVerify -and -not (Test-Path -LiteralPath $objdump)) { throw "objdump is missing in '$bin'; rerun with -NoVerify only to skip the checks." }
$cmakeExe = Resolve-CMake
Write-Host "   cmake: $cmakeExe"

if ($Clean -and (Test-Path -LiteralPath $BuildDirectory)) {
    Write-Step "Removing $BuildDirectory"
    Remove-Item -LiteralPath $BuildDirectory -Recurse -Force
}
$env:PATH = "$bin;$env:PATH"
$configure = @(
    '-S', $PSScriptRoot,
    '-B', $BuildDirectory,
    '-G', 'MinGW Makefiles',
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DCMAKE_CXX_COMPILER=$gxx",
    "-DCMAKE_MAKE_PROGRAM=$make",
    '-DCMAKE_SHARED_LINKER_FLAGS=-static -static-libgcc -static-libstdc++',
    '-DCMAKE_EXE_LINKER_FLAGS=-static -static-libgcc -static-libstdc++'
)
Write-Step "Configure ($Configuration, MinGW Makefiles)"
& $cmakeExe @configure
if ($LASTEXITCODE -ne 0) {
    throw 'CMake configure failed. When the directory was configured by another generator or toolchain, rerun with -Clean.'
}
Write-Step "Build ($([Environment]::ProcessorCount) jobs)"
& $cmakeExe '--build' $BuildDirectory '--parallel' "$([Environment]::ProcessorCount)"
if ($LASTEXITCODE -ne 0) { throw 'The Win32 bridge build failed.' }

$null = New-Item -ItemType Directory -Force -Path $Destination
$dll = Find-Artifact 'netdm_byond.dll'
$test = Find-Artifact 'netdm_bridge_test.exe'
$dllOut = Join-Path $Destination 'netdm_byond.dll'
$testOut = Join-Path $Destination 'netdm_bridge_test.exe'
Copy-Item -LiteralPath $dll -Destination $dllOut -Force
Copy-Item -LiteralPath $test -Destination $testOut -Force

$imports = @()
if (-not $NoVerify) {
    Write-Step 'Verify'
    Assert-I386PE $dllOut
    Assert-I386PE $testOut
    $imports = @(Assert-BridgeExports $dllOut $objdump)
    Write-Host "   PE machine i386, exports: $($exports -join ', ')"
}

$hash = (Get-FileHash -LiteralPath $dllOut -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $dllOut).Length
Write-Host ''
Write-Host ("NETDM_BRIDGE_READY arch=i386 dll={0} sha256={1} bytes={2} config={3}" -f $dllOut, $hash, $size, $Configuration)
Write-Host ("toolchain: {0}; {1}" -f $MinGw.Version, $bin)
if ($imports.Count) { Write-Host ("imports: {0}" -f ($imports -join ', ')) }
Write-Host ("test exe: {0}" -f $testOut)
Write-Host "integration test: pwsh -File scripts/test-byond-bridge.ps1 -BridgeBuild '$Destination'"

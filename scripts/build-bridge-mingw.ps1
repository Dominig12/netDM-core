param(
    [Parameter(Mandatory)] [string] $Destination,
    [string] $BuilderImage = 'ubuntu:24.04@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852'
)
# Cross-builds the Win32 call_ext bridge without a local Visual Studio. The
# DLL links libstdc++, libgcc and winpthread statically, so DreamDaemon does
# not need MinGW runtime DLLs next to it.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$native = Join-Path $root 'native/byond'
$output = [IO.Path]::GetFullPath($Destination)
$null = [IO.Directory]::CreateDirectory($output)
$script = @'
set -eu
apt-get update >/dev/null
apt-get install -y --no-install-recommends cmake make g++-mingw-w64-i686-posix binutils-mingw-w64-i686 >/dev/null
cmake -S /src -B /tmp/bridge -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=x86 \
  -DCMAKE_CXX_COMPILER=i686-w64-mingw32-g++-posix -DCMAKE_RC_COMPILER=i686-w64-mingw32-windres \
  "-DCMAKE_SHARED_LINKER_FLAGS=-static -static-libgcc -static-libstdc++" \
  "-DCMAKE_EXE_LINKER_FLAGS=-static -static-libgcc -static-libstdc++" >/dev/null
cmake --build /tmp/bridge -j2 >/dev/null
i686-w64-mingw32-objdump -p /tmp/bridge/netdm_byond.dll | grep -E 'DLL Name|netdm_' >&2
if i686-w64-mingw32-objdump -p /tmp/bridge/netdm_byond.dll | grep -Ei 'DLL Name: (libstdc|libgcc|libwinpthread)'; then
  echo 'MinGW runtime must be linked statically.' >&2
  exit 1
fi
i686-w64-mingw32-g++-posix --version | head -n 1 >&2
cp /tmp/bridge/netdm_byond.dll /tmp/bridge/netdm_bridge_test.exe /out/
'@
docker run --rm --network bridge `
    --mount "type=bind,source=$native,target=/src,readonly" `
    --mount "type=bind,source=$output,target=/out" `
    $BuilderImage /bin/sh -c $script
if ($LASTEXITCODE -ne 0) { throw 'MinGW Win32 bridge build failed.' }
$dll = Join-Path $output 'netdm_byond.dll'
$bytes = [IO.File]::ReadAllBytes($dll)
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x14C) { throw 'Bridge is not an i386 PE image.' }
Write-Host "Win32 bridge: $dll sha256=$((Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant())"

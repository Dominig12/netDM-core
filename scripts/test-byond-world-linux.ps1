param(
    [Parameter(Mandatory)] [string] $ByondImage,
    [Parameter(Mandatory)] [string] $NativeBuilderImage,
    [string] $SdkImage = 'mcr.microsoft.com/dotnet/sdk:10.0'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$native = Join-Path $root 'native/byond'
$work = [IO.Directory]::CreateTempSubdirectory('netdm-linux-world-').FullName
$suffix = [guid]::NewGuid().ToString('N')
$volume = "netdm-world-$suffix"
$hostName = "netdm-world-host-$suffix"
$gameName = "netdm-world-game-$suffix"
Copy-Item -LiteralPath (Join-Path $root 'tests/byond-world/smoke.dme'),
    (Join-Path $root 'tests/byond-world/smoke.dm'), (Join-Path $root 'dm/netdm_bridge.dm'),
    (Join-Path $root 'dm/netdm_services.dm') -Destination $work
Set-Content -LiteralPath (Join-Path $work 'bridge-path.dm') -Value '#define NETDM_DLL "./netdm_byond.so"', '#define NETDM_CRASH_SMOKE'

function Wait-Probe([string] $Description, [scriptblock] $Probe) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Probe) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $Description."
}

function Test-Ready([string] $Kind, [int] $ExpectedCode) {
    $code = docker exec $hostName curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:5087/api/readiness/$Kind" 2>$null
    return $LASTEXITCODE -eq 0 -and $code -eq "$ExpectedCode"
}

docker volume create $volume | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'IPC volume creation failed.' }
try {
    docker run --rm --network none --entrypoint /bin/sh `
        --mount "type=bind,source=$native,target=/src,readonly" `
        --mount "type=bind,source=$work,target=/out" `
        $NativeBuilderImage -ec 'cmake -S /src -B /tmp/bridge -DCMAKE_CXX_FLAGS=-m32 -DCMAKE_BUILD_TYPE=Release; cmake --build /tmp/bridge -j2; cp /tmp/bridge/netdm_byond.so /out/'
    if ($LASTEXITCODE -ne 0) { throw 'ELF32 bridge build failed.' }

    docker run -d --network none --name $hostName --entrypoint /bin/sh `
        --mount "type=volume,source=$volume,target=/ipc" `
        --mount "type=bind,source=$root,target=/repo,readonly" `
        --env NETDM_IPC_MODE=v1 --env NETDM_SOCKET_PATH=/ipc/run/core.sock `
        $SdkImage -ec 'if ! test -f /work/src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll; then mkdir -p /work; tar -C /repo --exclude=bin --exclude=obj -cf - Directory.Build.props src | tar -C /work -xf -; dotnet build /work/src/NetDM.Host -c Release --nologo; fi; install -d -m 700 /ipc/run; cd /work/src/NetDM.Host; exec dotnet bin/Release/net10.0/NetDM.Host.dll' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Host container launch failed.' }
    Wait-Probe 'initial IPC readiness' { Test-Ready 'ipc' 200 }
    if (-not (Test-Ready 'game' 503)) { throw 'Game became ready before Hello.' }

    docker run -d --network none --name $gameName --entrypoint /bin/sh `
        --mount "type=volume,source=$volume,target=/ipc" `
        --mount "type=bind,source=$work,target=/out" `
        --env NETDM_IPC_MODE=v1 --env NETDM_SOCKET_PATH=/ipc/run/core.sock `
        $ByondImage -ec 'mkdir /tmp/smoke; cd /tmp/smoke; cp /out/*.dm /out/*.dme /out/*.so .; DreamMaker smoke.dme; result=0; timeout -k 2s 85s DreamDaemon smoke.dmb -ports 51339 -trusted -invisible >daemon.log 2>&1 || result=$?; cp ./*.log /out/; cat daemon.log; cat smoke.log; test "$result" -eq 0; grep -qx NETDM_WORLD_PASS smoke.log; ! grep -Ei "NETDM_WORLD_FAIL|runtime error" smoke.log' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'DreamDaemon container launch failed.' }
    Wait-Probe 'snapshot and delivery before crash' { Test-Path -LiteralPath (Join-Path $work 'crash.ready') }
    if (-not (Test-Ready 'game' 200)) { throw 'Initial snapshot did not make game ready.' }

    docker kill --signal KILL $hostName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not kill the isolated Host.' }
    if ((docker inspect -f '{{.State.ExitCode}}' $hostName) -ne '137') { throw 'Host was not killed by SIGKILL.' }
    Wait-Probe 'DreamDaemon observing the crash' { Test-Path -LiteralPath (Join-Path $work 'offline.ready') }
    docker exec $gameName test -S /ipc/run/core.sock
    if ($LASTEXITCODE -ne 0) { throw 'Expected the stale socket in the shared volume.' }
    docker start $hostName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Host restart failed.' }
    Wait-Probe 'IPC readiness on the same socket path' { Test-Ready 'ipc' 200 }
    if (-not (Test-Ready 'game' 503)) { throw 'Restarted Host reported game ready before resnapshot.' }
    docker exec $gameName sh -c 'printf ready > /tmp/smoke/restart.ready'
    if ($LASTEXITCODE -ne 0) { throw 'Could not release the DreamDaemon restart gate.' }
    Wait-Probe 'snapshot and delivery after restart' { Test-Path -LiteralPath (Join-Path $work 'after.ready') }
    if (-not (Test-Ready 'game' 200)) { throw 'Resnapshot did not restore game readiness.' }
    docker exec $gameName sh -c 'printf ready > /tmp/smoke/finish.ready'
    if ($LASTEXITCODE -ne 0) { throw 'Could not release the DreamDaemon completion gate.' }
    Wait-Probe 'DreamDaemon completion' {
        (docker inspect -f '{{.State.Running}}' $gameName 2>$null) -eq 'false'
    }
    if ((docker inspect -f '{{.State.ExitCode}}' $gameName) -ne '0') { throw 'Real Linux DreamDaemon smoke failed.' }
    Write-Host 'NETDM_CRASH_RECOVERY_PASS'
}
finally {
    docker exec $gameName cat /tmp/smoke/smoke.log 2>$null
    docker logs --tail 50 $gameName 2>$null
    docker logs --tail 50 $hostName 2>$null
    docker rm -f $gameName $hostName 2>$null | Out-Null
    docker volume rm $volume 2>$null | Out-Null
    Write-Host "Smoke artifacts: $work"
}

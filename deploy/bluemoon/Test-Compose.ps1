param(
    [Parameter(Mandatory)] [string] $EnvironmentFile,
    [Parameter(Mandatory)] [string] $ProjectName,
    # Written only after acceptance passes; it pins the exact tested image IDs.
    [string] $ManifestPath
)
$ErrorActionPreference = 'Stop'
$composeFile = Join-Path $PSScriptRoot 'compose.yaml'
$options = @('compose', '-f', $composeFile, '--env-file', $EnvironmentFile, '-p', $ProjectName)

function Compose {
    docker @options @args
    if ($LASTEXITCODE -ne 0) { throw "Compose command failed: $($args[0])" }
}

function Read-Summary {
    return Compose exec -T netdm curl -fsS http://127.0.0.1:5087/api/game/summary | ConvertFrom-Json
}

function Read-RoundCount {
    return [int](Compose exec -T db sh -ec 'export MYSQL_PWD="$(cat /run/secrets/db_password)"; mariadb -ubluemoon -Dbluemoon -Nse "SELECT COUNT(*) FROM round"')
}

if (Compose ps -aq) { throw 'Use a fresh Compose project for acceptance.' }
try {
    Compose up -d --no-build --wait --wait-timeout 900
    $initial = Read-Summary
    if ($initial.nodes -lt 1) { throw 'Observe did not publish any APCs.' }
    $game = Compose ps -q game
    $db = Compose ps -q db
    $hostId = Compose ps -q netdm
    $rounds = Read-RoundCount
    if ($rounds -lt 1) { throw 'BlueMoon did not initialize a database round.' }
    $byondVersion = (Compose exec -T game DreamDaemon -version 2>&1 | Out-String).Trim()
    Write-Host $byondVersion
    if ($byondVersion -notmatch '516\.1688') { throw 'Expected DreamDaemon 516.1688 in the game image.' }
    $gameSource = (Compose exec -T game cat netdm-source.json | Out-String) | ConvertFrom-Json
    Compose exec -T db /usr/local/bin/bluemoon-check-db
    $restricted = Compose exec -T game curl -s -o /dev/null -w '%{http_code}' http://netdm:5088/api/metrics
    if ($restricted -ne '404') { throw 'The internal readiness listener exposes Observatory.' }

    $restarts = [int](docker inspect -f '{{.RestartCount}}' $hostId)
    docker kill --signal KILL $hostId | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Expected Host SIGKILL.' }
    # The restart policy may revive the Host before inspection; either outcome is a crash.
    $killed = docker inspect -f '{{.State.ExitCode}} {{.RestartCount}}' $hostId
    if ($killed -notmatch '^137 ' -and [int]($killed -split ' ')[1] -le $restarts) { throw "Expected Host SIGKILL, got '$killed'." }
    Compose exec -T game test -S /run/netdm/session/core.sock
    Compose up -d --no-build --no-deps --force-recreate netdm
    $recovered = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $candidate = Read-Summary
            if ($candidate.streamId -ne $initial.streamId -and $candidate.nodes -gt 0) {
                $null = Compose exec -T game curl -fsS http://netdm:5088/api/readiness/game
                $recovered = $candidate
                break
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (!$recovered -or (Compose ps -q game) -ne $game -or (Compose ps -q db) -ne $db -or (Read-RoundCount) -ne $rounds) {
        throw 'Host recovery did not preserve the game and database round.'
    }
    Compose stop game
    if ((docker inspect -f '{{.State.ExitCode}}' $game) -ne '0') { throw 'DreamDaemon did not stop cleanly.' }
    $closedRounds = [int](Compose exec -T db sh -ec 'export MYSQL_PWD="$(cat /run/secrets/db_password)"; mariadb -ubluemoon -Dbluemoon -Nse "SELECT COUNT(*) FROM round WHERE shutdown_datetime IS NOT NULL"')
    if ($closedRounds -lt 1) { throw 'DreamDaemon did not write the round shutdown to MariaDB.' }
    Write-Host "BLUEMOON_COMPOSE_PASS nodes=$($initial.nodes) restored=$($recovered.nodes) rounds=$rounds"

    if ($ManifestPath) {
        $pins = foreach ($dockerfile in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.Dockerfile') {
            foreach ($match in Select-String -LiteralPath $dockerfile.FullName -Pattern '(?:ARG \w+=|FROM )(\S+@sha256:[0-9a-f]{64})') {
                [ordered]@{ dockerfile = $dockerfile.Name; image = $match.Matches[0].Groups[1].Value }
            }
        }
        $repository = Join-Path $PSScriptRoot '../..'
        [ordered]@{
            platform = 'linux-x64-compose'
            game = $gameSource
            coreCommit = (git -C $repository rev-parse HEAD)
            coreTreeDirty = [bool](git -C $repository status --porcelain)
            protocol = '1.1'
            byond = $byondVersion
            images = [ordered]@{
                game = (docker inspect -f '{{.Image}}' $game)
                netdm = (docker inspect -f '{{.Image}}' (Compose ps -q netdm))
                db = (docker inspect -f '{{.Image}}' $db)
            }
            basePins = @($pins)
            acceptance = [ordered]@{ marker = 'BLUEMOON_COMPOSE_PASS'; nodes = $initial.nodes
                restored = $recovered.nodes; rounds = $rounds; at = [DateTime]::UtcNow.ToString('o') }
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding utf8
        Write-Host "Release manifest: $ManifestPath"
    }
}
finally { Compose stop }

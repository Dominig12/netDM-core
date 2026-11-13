param(
    # MetaStation and Box Station have telecomms and a message server; runtime is the small debug station.
    [ValidateSet('metastation', 'boxstation', 'runtime')] [string] $Map = 'metastation',
    # One switch for research, messenger and radio; each can be overridden below.
    [ValidateSet('shadow', 'authoritative')] [string] $Mode = 'authoritative',
    [ValidateSet('', 'shadow', 'authoritative')] [string] $ResearchMode = '',
    [ValidateSet('', 'shadow', 'authoritative')] [string] $MessengerMode = '',
    [ValidateSet('', 'shadow', 'authoritative')] [string] $RadioMode = '',
    [int] $GamePort = 51515,
    [int] $Minutes = 240,
    # Skip the Host and DMB builds and start what was built last time.
    [switch] $NoBuild,
    [string] $GameRepository = '..\MOLOT-BlueMoon-Station',
    [string] $BuildDirectory = 'TestResults\p1\game-src',
    [string] $BridgeDll = 'TestResults\p3-bridge\netdm_byond.dll'
)
# One command for a manual check in a real BYOND client: builds the netDM Host and a playable BlueMoon
# DMB with TGUI, picks the map, starts both with the chosen netDM modes and prints where to connect.
# Run from the netDM-core root:  pwsh -File scripts\play-bluemoon.ps1
# Stop with Ctrl+C, by creating <BuildDirectory>\netdm-stand.stop, or after -Minutes.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
foreach ($path in $BuildDirectory, $BridgeDll, $GameRepository) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Not found: $path. Run from the netDM-core root after the BlueMoon export." }
}
$dreamDaemon = Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin/dd.exe'
if (-not (Test-Path -LiteralPath $dreamDaemon)) { throw "BYOND is not installed: $dreamDaemon" }
$research = if ($ResearchMode) { $ResearchMode } else { $Mode }
$messenger = if ($MessengerMode) { $MessengerMode } else { $Mode }
$radio = if ($RadioMode) { $RadioMode } else { $Mode }

if (-not $NoBuild) {
    Write-Host '== Building the netDM Host'
    dotnet build src/NetDM.Host -c Release -v quiet -nologo | Select-String -Pattern ' error |Build succeeded' | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw 'The Host build failed.' }
    # A playable world: no unit tests; the runtime station is forced by LOWMEMORYMODE, other maps by next_map.json.
    $defines = if ($Map -eq 'runtime') { 'CBT,LOWMEMORYMODE' } else { 'CBT' }
    Write-Host "== Building BlueMoon ($defines), a few minutes"
    pwsh -NoProfile -File scripts\build-bluemoon-unit-dmb.ps1 -GameRepository $GameRepository -BuildDirectory $BuildDirectory `
        -Defines $defines -WithTgui 2>&1 | Select-String -Pattern 'errors|DreamMaker failed' | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw 'The DMB build failed.' }
}
if ($Map -ne 'runtime') {
    Copy-Item -LiteralPath (Join-Path $BuildDirectory "data/netdm-audit/$Map.json") -Destination (Join-Path $BuildDirectory 'data/next_map.json') -Force
}

Write-Host @"

== Стенд netDM: карта=$Map исследования=$research мессенджер=$messenger радио=$radio
Подключение: DreamSeeker -> Open location -> byond://127.0.0.1:$GamePort
Локальное подключение получает права админа. Раунд: в лобби Start Now (вкладка Server) или Ready.

Что проверить:
  Исследования ($research)
    - Консоль R&D: исследовать узел; очки сервера консоли уменьшаются один раз.
    - ПКМ по консоли -> "Собрать очки на сервер": перевод с другого сервера сети.
    - Диск: "Download to disk", затем "Upload disk" на другой консоли — только знания, без очков.
  Мессенджер ($messenger)
    - Два КПК: сообщение приходит один раз, "Ответ" и "Блок" работают.
    - Выключить message server (серверная телекомма): "ERROR: Server is not responding."
    - КПК вдали от APC (космос, глухие техтоннели): отправителю приходит "not delivered".
  Радио ($radio)
    - Говорить в общий канал из разных отделов: слышат все.
    - Вдали от APC гарнитура не дотягивается до телекомма; обычная рация через 2 с шлёт резерв.
    - Выключить приёмники телекомма: речь по подпространству никто не слышит.
  Админ: вкладка Debug -> "netDM Topology" рисует сеть; строки состояния netDM в логе мира.

Логи: $BuildDirectory\netdm-stand-*.log и $BuildDirectory\data\logs\
Остановка: Ctrl+C или файл $BuildDirectory\netdm-stand.stop
"@
pwsh -NoProfile -File scripts\start-bluemoon-stand.ps1 -BuildDirectory $BuildDirectory -BridgeDll $BridgeDll -GamePort $GamePort `
    -Minutes $Minutes -ResearchMode $research -MessengerMode $messenger -RadioMode $radio -DreamDaemon $dreamDaemon

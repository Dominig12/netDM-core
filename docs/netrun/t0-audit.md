# T0 — Аудит исходных репозиториев NETRUN / NETDM

Дата аудита: 25.11.2026. Аудит выполнен на подтверждённых рабочих ветках, дерево до и после чистое.

| Репозиторий | Ветка | HEAD на момент аудита |
|---|---|---|
| `netDM-core` | `feature/byond-native-bridge` | `390c3ef` |
| `MOLOT-BlueMoon-Station` | `net-feature-dmcore-new` | `028dc25ef3` |

Целевая спецификация: `NETRUN_Implementation_Specification_RU.md` (v1.0). Этот документ — факты о коде, а не о целевой модели. Целевая модель — [architecture.md](architecture.md), решения — [adr/](adr/), матрица миграции — [migration-matrix.md](migration-matrix.md), текущее состояние — [status.md](status.md).

## 1. Подтверждение доступа и окружения

| Проверка | Результат |
|---|---|
| `git rev-parse --abbrev-ref HEAD` | обе ветки совпадают с ожидаемыми |
| `git status --short` | пусто в обоих репозиториях |
| .NET SDK | 10.0.403 (также 8.0.127, 8.0.421) |
| BYOND DreamMaker | 516.1688, `C:\Program Files (x86)\BYOND\bin` |
| Node / npm | 24.16.0 / 11.13.0 |
| yarn | `tgui/.yarn/releases/yarn-4.18.1.cjs` (yarn не в PATH) |
| Docker | CLI 29.9.2, демон **не запущен** |
| C/C++ toolchain | **отсутствует**: нет `cl.exe`, `gcc`, `mingw`, `clang`, `zig`; VS C++ не установлен |

**Следствие:** нативный мост `netdm_byond.dll` в дереве отсутствует и не может быть собран на этой машине. Любая проверка в живом DreamDaemon заблокирована до появления тулчейна или готовой DLL. Это блокер проверки, а не основание считать соответствующие требования выполненными.

## 2. Воспроизводимый baseline

Все команды выполнены на указанных HEAD; логи — в `TestResults/baseline/` (вне git).

| Команда | Что проверяет | Ожидаемый маркер | Результат |
|---|---|---|---|
| `dotnet build src/NetDM.Host -c Release` | Core+Services+Host, `TreatWarningsAsErrors` | `0 Warning(s) 0 Error(s)` | **PASS**, 10.8 с |
| `dotnet run --project tests/NetDM.SmokeTests -c Release` | юнит/smoke-харнесс | `All netDM-core smoke tests passed.` | **PASS**, exit 0, ~6 с |
| `pwsh -NoProfile -File tests/host-ipc.ps1` | живой Host через V1-канал | `NETDM_IPC_PASS` | **PASS** после исправления ожидания контракта (см. §2.1) |
| `pwsh -File scripts/Export-Source.ps1` + `build-bluemoon-unit-dmb.ps1 -GameRepository ..\MOLOT-BlueMoon-Station` | герметичный DM-мир с netDM-файлами | `UNIT_DMB_READY` и `0 errors, 0 warnings` | **PASS**, 267 с |
| `dm.exe smoke.dme` (копия `tests/byond-world` + `dm/*.dm`) | DM-тулчейн и DM-часть моста | `- 0 errors, 0 warnings` | **PASS**, 0.6 с |
| `scripts/test-byond-world.ps1`, `test-bluemoon-unit.ps1`, `test-byond-bridge.ps1` | живой мир / ABI моста | `NETDM_WORLD_PASS` | **НЕ ВЫПОЛНЕНО** — нет DLL и тулчейна |
| `scripts/check-all.ps1 -Only world` / `-Only bridge` | группы проверок стенда | — | **НЕ ВЫПОЛНЕНО** — та же причина |

`dotnet build` в корне репозитория падает с `MSB1003`: в решении нет `.sln`, сборка идёт по csproj. Это не регрессия, а свойство репозитория (см. ADR-0004).

### 2.1 Найденная и исправленная регрессия

`tests/host-ipc.ps1:1062` ожидал `commands -ceq 'set_state'`, тогда как HEAD `390c3ef` добавил команду `machine.door/control` (`src/NetDM.Services/Contracts/MachinesContracts.cs:56`), и `ServiceRuntime.Descriptor` отдаёт `set_state,control` (`ServiceRuntime.cs:1412`). Ожидание приведено к фактическому контракту; проверка остаётся строгой (полное совпадение списка). Повторный прогон: `NETDM_IPC_PASS`, exit 0.

## 3. Карта решения netDM-core

Логических проектов три; `.sln` нет.

| Проект | Назначение | Зависимости | LOC (C#) | Владеет состоянием |
|---|---|---|---|---|
| `NetDM.Core` | транспорт: неизменяемая топология, маршрутизация (Dijkstra/A*/CSR), разделяемая радиосреда, пакетный движок, CPU-планировщик | — | 5 839 | `TopologyStore`, `SharedMediumNetwork`, `RadioPacketTransport`, `PriorityWorkScheduler` |
| `NetDM.Services` | сервисный слой: схемы, каталог, подписки, команды/результаты, доставки, application-ACK | Core | 5 375 | `ServiceRuntime` (один lock `_gate`), `SchemaRegistry`, `ResearchLedger`, `NetShell` |
| `NetDM.Host` | исполняемый композиционный корень: Kestrel, IPC, игровая сессия, фоновые циклы | Core, Services | 9 398 | `GameNetworkService.Session`, per-connection staged-снимок, `ResearchHost`, `NetworkSpace`, `ShellPrograms` |
| `tests/NetDM.SmokeTests` | консольный харнесс (не xUnit) | Core, Services | 5 237 | — |

Всего ~26 тыс. строк C#. Внешних NuGet-зависимостей нет: только BCL и ASP.NET Core shared framework.

### 3.1 Модель потоков (полный перечень)

| Источник | Где | Что мутирует | Синхронизация |
|---|---|---|---|
| Kestrel/HTTP | `Program.cs:85-338` | читает рантаймы | без блокировок, только чтение |
| IPC accept-цикл | `PipeHostedService.cs:76-120` | `_ipcReady`, слоты соединений | `SemaphoreSlim` (16) |
| Read loop на соединение (≤16) | `:173-457` | staged-состояние сессии, захват `_gameOwner` | один писатель на соединение |
| Writer task на соединение | `:234-271` | очередь ответов | `Channel<byte[]>` 256 кадров / 2 МиБ |
| Fire-and-forget запросы (≤64/соединение) | `:378-405` | транспорт через `GameNetworkService` | `lock(_sessionGate)`, `lock(_drainGate)` |
| `PacketTickRunner` (LongRunning, 100 мс) | `PacketTickRunner.cs:81-152` | очереди пакетов, квитанции | `RadioPacketTransport._gate` → `_airtimeLock` → `ServiceRuntime._gate` |
| Reroute worker (20 мс) | `GameNetworkService.cs:180,492-531` | `_inflight` | `_gate` транспорта |
| `PriorityWorkScheduler` (1..8) | `PriorityWorkScheduler.cs` | выполняет `Route` | `_gate`, `_budgetGate` |
| `ResearchHost` `PeriodicTimer(20 мс)` | `ResearchHost.cs:432-466` | леджер, `ShellPrograms.Tick`, `NetworkSpace.Tick` | `ResearchHost._gate`; статические `ShellPrograms`/`NetworkSpace` — без lock |
| Бенчмарки | `Benchmarks/*` | синтетический граф | свои `_cts`; в игровой путь не входят |
| Нативный шим (C++) | `native/byond/netdm_bridge.cpp` | out/in буферы | `std::mutex` |

Ни один `lock` не удерживается через `await`/IO — проверено по всем вхождениям. Порядок блокировок: `_sessionGate` → `_writer`/`_airtimeLock` → `RadioPacketTransport._gate` → `ServiceRuntime._gate`; обратного пути нет.

### 3.2 Очереди и backpressure

Ограничены: CPU-очереди (256), IPC outbox (256 кадров/2 МиБ), in-flight запросов (64), соединения (16), пакеты (4096 + резерв интерактива 128), reroute-тикеты (64), inbox (4096 записей/4 МиБ), outbox доставок (8192), операции (16384), host-команды (256), staged-снимок (512 чанков/32 768 фактов), дельта (4096), нативные буферы (256/1024).

Не ограничены или не освобождаются: `SharedMediumNetwork._airtime` (ключ включает произвольный `uint`-канал, записи не удаляются), `NetworkRuntime._traffic`, `ServiceRuntime._endpointGenerations`, `ServiceRuntime._scans`, статические `ShellPrograms.Budgets` / `NetworkSpace.Operators`. Эти пункты внесены в [status.md](status.md) как технический долг; часть касается NETDM, часть — игрового слоя.

### 3.3 IPC

Режимы `legacy` и `v1`. Framed: 4-байтный BE заголовок, кадр ≤ 48 КиБ (`IpcFrames.cs:7`). Handshake: major 1, minor 1 = staged. Полный перечень DM→CORE операций и CORE→DM конвертов зафиксирован в аудите; ключевые группы: staged-снимок (`snapshot_*`, `area_update_*`, `world_delta`, `heartbeat`), сервисы (`endpoint_*`, `service_register`, `publish`, `invoke`, `discover`, `subscribe`, `unsubscribe`, `delivery_*`, `command_status`), пакеты (`packet_send`, `packet_receipts`, `packet_cancel`), и модульные control-plane op (`research_*`, `radio_reach`, `network_life`).

### 3.4 Точки расширения

| Точка | Файл | Назначение | Состояние |
|---|---|---|---|
| `IHostIpcModule` + `HostIpcModuleRouter` | `Transport/HostIpcModules.cs:10-48` | новый DM→CORE control-plane op | работает, 3 реализации |
| `IHostServiceExtension` | `Transport/HostServiceExtensions.cs:11-20` | обработчик команды, дошедшей до Host-провайдера | работает, 3 реализации |
| `IServiceContractModule` | `Contracts/IServiceContractModule.cs:8-11` | регистрация схем/команд игры | **механизм есть, реализаций в продакшене нет**: все контракты зашиты в `SchemaRegistry.CreateDefault` (`Schemas.cs:82-91`) |
| `INetworkBotBehavior` | `Services/NetworkBotBehaviors.cs` | поведение сетевых NPC | в `Program.cs` не зарегистрирован ни один |

## 4. Карта игровой интеграции (MOLOT)

`code/modules/netdm/` — 19 `.dm`, 9 902 строки; подсистема `code/controllers/subsystem/netdm_observe.dm` — 863 строки.

### 4.1 Две независимые «полосы»

* **map lane** — `snapshot_*`, `area_update_*`, `world_delta`, `heartbeat`, `topology_view`; один слот `pending` (20 с), полная ресинхронизация при любом таймауте.
* **service lane** — `endpoint_*`, `service_register`, `publish/invoke/deliver_*`; до 16 запросов в полёте (`netdm_service_lane.dm:4`).

Подсистема файрит до 10 раз/с (`wait = 1`, не `SS_BACKGROUND`), один файр — `pump(16)` и до 16 сообщений; карта публикуется `snapshot_*`/`area_update_*` чанками по 64 факта с md5-манифестом.

### 4.2 Инвентарь устройств и сервисов

| Область | Устройство | Точка регистрации | Сервис / схема | Режим | Авторитетное состояние |
|---|---|---|---|---|---|
| R&D | `/obj/machinery/rnd/server` | `research/server.dm:42` | `research.<storage>` / `research.server` | authoritative после sync | Host-леджер |
| R&D | `/obj/machinery/computer/rdconsole` | `research/rdconsole.dm:106` | инициатор | — | Host (через invoke) |
| R&D | производство (`rnd/production`) | `research/machinery/_production.dm:42` | — | узел | локальный техуэб |
| PDA | `/obj/item/modular_computer/pda` | `pda.dm:169` | `netshell.<ep>` / `network.shell` | authoritative | Host (shell) |
| PDA | любой компьютер с мессенджером | `messenger_shadow.dm:59` | `pda.<ep>` / `pda.messenger` | authoritative | Host-сервер сообщений |
| PDA | `/obj/machinery/telecomms/message_server` | `message_server.dm:111` | `msgsrv.<ep>` / `pda.server` | authoritative | Host |
| Радио | `/obj/item/radio`, `/obj/machinery/telecomms` | `radio/radio.dm:141`, `telecomunications.dm:95` | `rx.<ep>` / `radio.receiver` | uplink по env | приёмник (legacy-цепочка) |
| Камеры | `/obj/machinery/camera`, `computer/security` | `camera/camera.dm:91`, `computer/camera.dm:49` | `camera.<ep>` / `camera.access` | только по env | объект камеры |
| Двери | `/obj/machinery/door`, `/obj/machinery/button` | `doors/door.dm:84`, `buttons.dm:53` | `door.<ep>` / `machine.door` | authoritative жёстко | Host (команда), дверь — исполнитель |
| Атмос | `air_sensor`, `computer/atmos_control`, `airalarm` | `atmos_control.dm:112,172`, `atmos_telemetry.dm:427` | `atmos.telemetry`, `atmos.alarm` | shadow (+команды) | сенсор/тревога локально |
| Питание | `/obj/machinery/power/apc` | `apc_map.dm`, `power/apc.dm:333` | `netshell.<ep>`, `netdiag.*` | узел карты | мир (АПЦ) |
| Прочее | имплант, адаптер, консольные операторы, сущность | `network_terminal.dm:713,764`, `network_space_actor.dm` | `network.shell` | — | предмет/роль |

### 4.3 Потребители `techweb_holder`

Под леджером два пути записи: (а) хуки `code/modules/research/techweb/netdm_research_hooks.dm` через примитивы `add_point_list` / `add_point_type` / `remove_point_list` / `modify_point_list` / `research_node` / `commit_income` / `boost_with_path` / `get_price`; (б) **прямые записи в поля техуэба в обход хуков**, минимум одна: `modular_bluemoon/white_steel/inner/super_cruise/orbital_poi_generator/loot/research_disks.dm:11` (`stored_research.researched_nodes[node_id] = TRUE`). Это нарушение INT-RD-001 и занесено в матрицу миграции как `replace`.

### 4.4 UI

Из DM создаются ровно два интерфейса:

* `NetdmSpace` (`network_space.dm:173`) — окно присутствия/карты для игрока, самовзводящийся DM-таймер 2 с, отправляет `knowledge` + `presence` + `chat_read` + `life` на каждое открытое окно.
* `NetdmTopology` (`topology_view.dm:29`) — административный read-only граф, `set_autoupdate(FALSE)`, push по новой версии.

Остальные `tgui/packages/tgui/interfaces/Netdm*` — подкомпоненты этих двух; отдельно из DM не инстанцируются.

### 4.5 Тесты

`code/modules/unit_tests/netdm_observe.dm` (3 031 строка, ~30 тестов) требует живой Host и `NETDM_IPC_MODE=v1`; без DLL не запускается. Список в `netDM-core/scripts/bluemoon-netdm-tests.txt` **устарел**: 5 из 23 путей переименованы и в списке отсутствуют актуальные имена (`messenger_authoritative`, `radio_uplink`/`radio_station`, `door_station`/`netdm_door_dispatch`, `research_authoritative`). Исправление списка внесено в [status.md](status.md).

## 5. Соответствие целевым инвариантам (сводка)

Полная матрица — [migration-matrix.md](migration-matrix.md). Кратко:

| Группа MUST | Состояние на момент аудита |
|---|---|
| NET-001…009 (топология, relay) | частично: граф АПЦ/радио/секторов есть, отдельного объекта «NTnet Relay» с beacons/peering и внешними uplink-политиками нет |
| HOST-001 (идемпотентная регистрация, поколения) | выполнено для узлов и эндпойнтов; для цифровых хостов NETRUN — не начато |
| SVC-001 (полное описание операции) | частично: схемы/версии/типы есть; прав, стоимости, cooldown, дальности и документации в дескрипторе нет |
| SVC-002 (идемпотентность, таймауты, отмена) | выполнено для сервисного слоя (ключ+digest, `Duplicate`, `Unknown`, без слепого повтора) |
| SVC-003 (права на границе commit, fencing) | **не выполнено**: только `AccessScope` (none/domain/open) на discover/subscribe/invoke |
| PRIM-001/002/003/004 (реестр действий, селекторы, эффекты, события) | **не начато** как слой NETRUN; частично эмерджентно в shell-командах |
| PLAY-001 (движение по графу) | выполнено в C# (`NetworkSpace`): маршрут, транзит, отмена, потеря маршрута |
| SEC-001/002 (Identity/Presence/Capability) | **не выполнено**: нет разделения capability и нет отзыва с fencing |
| RES-001 (виртуальные ресурсы, lease, атомарность) | **не выполнено**: CU/RAM/Storage/Bandwidth отсутствуют, кроме бюджета состояния программ |
| SCRIPT-001/002/003 (общий IR, песочница, импорт) | частично: shell-язык и `ShellPrograms` с бюджетами; общего IR, VM-кванта и импорта нет |
| AGENT-001 (квоты и общие примитивы) | частично: `NetworkLifeRuntime` и боты без общего реестра действий |
| LIFE-001 (единственность ядра, миграция) | не начато как отдельная модель; присутствие/миграция есть в `NetworkSpace` |
| WORLD-001 (уровни детализации) | не начато |
| UI-001/002/003 (вкладки, независимые состояния, реальный трафик) | **не выполнено**: одно окно `NetdmSpace` без боковых вкладок, карта административная и read-only |
| ARCH-001/002 (один владелец, расширения) | ARCH-001 выполнено в транспорте; ARCH-002 — механизм есть, но контракты игры зашиты, а ~5 тыс. строк игрового контента лежат в `NetDM.Services`/`NetDM.Host` |
| ARCH-003/004 (один writer, bounded очереди) | ARCH-003 выполнено в транспорте; ARCH-004 выполнено для IPC/пакетов/сервисов, не выполнено для перечисленных в §3.2 структур |
| INT-001/002 (нет shadow-пути, нет фиктивного успеха) | **нарушено**: при выключенном netDM речь (`radio_uplink.dm:263-270`) и PDA-сообщения (`messenger_relay.dm:506-515`) не доходят вовсе, кнопки/двери не работают (`doorcontrol.dm:41-50`); обещанного legacy-фолбэка PDA нет |

## 6. Выводы аудита

1. **Транспорт и сервисный слой пригодны к использованию.** Один писатель на структуру, иммутабельные снапшоты, отсутствие блокировок через `await`, ограниченные очереди, идемпотентность и application-ACK. Их не переписываем.
2. **Игровой слой отсутствует как слой.** Ресурсы, права, эффекты, реестр действий, VM и клиентский интерфейс NETRUN не реализованы; часть игрового контента эмерджентно живёт внутри `NetDM.Services`/`NetDM.Host`.
3. **Расширяемость объявлена, но не используется:** `IServiceContractModule` и `INetworkBotBehavior` не подключены, контракты SS13 зашиты в `SchemaRegistry.CreateDefault`.
4. **Есть нарушения инвариантов, которые нужно исправить в ближайших этапах:** отсутствие legacy-пути при выключенном netDM (INT-002), прямой обход леджера в техуэбе (INT-RD-001), утечка скрытого состояния в `ui_data` (UI-003), неограниченные структуры в §3.2 (ARCH-004).
5. **Проверка в живом мире заблокирована** отсутствием нативного моста; сборка DM и весь C#-слой проверяемы.

Эти выводы определяют порядок работ в [status.md](status.md) и [roadmap.md](roadmap.md).

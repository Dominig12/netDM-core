# Состояние реализации NETRUN

Обновлено 25.11.2026. Источник требований — `NETRUN_Implementation_Specification_RU.md`; порядок работ — [roadmap.md](roadmap.md); архитектура — [architecture.md](architecture.md); факты аудита — [t0-audit.md](t0-audit.md).

Ветки: `netDM-core@feature/byond-native-bridge`, `MOLOT-BlueMoon-Station@net-feature-dmcore-new`.

## 1. Ограничения стенда (действуют на всё ниже)

| Ограничение | Следствие |
|---|---|
| Нет C/C++ тулчейна (`cl`, `gcc`, `mingw`, `clang`, `zig`), Docker-демон не используется | `netdm_byond.dll`/`.so` собрать нельзя; моста в дереве нет; **проверка в живом DreamDaemon заблокирована** |
| `netdM-core` без `.sln` | сборка по csproj: `dotnet build src/NetDM.Host -c Release` |
| Тестовый фреймворк не используется | харнессы — консольные приложения с маркером и кодом возврата |

Требования, для которых нужен живой мир, помечены **«не проверено — блокер стенда»** и не считаются выполненными.

## 2. Что сделано

### N0. T0-аудит — выполнено

| Артефакт | Файл |
|---|---|
| Карта решения, модель потоков, очереди, IPC, точки расширения, пробелы | [t0-audit.md](t0-audit.md) |
| Матрица reuse/adapt/replace/remove/defer | [migration-matrix.md](migration-matrix.md) |
| Целевая архитектура и владение состоянием | [architecture.md](architecture.md) |
| Решения | [adr/](adr/README.md) |
| План и gates | [roadmap.md](roadmap.md) |

Воспроизводимые проверки и фактические результаты:

| Команда | Результат |
|---|---|
| `dotnet build src/NetDM.Host -c Release` | PASS, 0 предупреждений, 0 ошибок |
| `dotnet run --project tests/NetDM.SmokeTests -c Release` | PASS, `All netDM-core smoke tests passed.` |
| `pwsh -NoProfile -File tests/host-ipc.ps1` | PASS, `NETDM_IPC_PASS` (после исправления ожидания контракта) |
| герметичная DM-сборка (`Export-Source.ps1` + `build-bluemoon-unit-dmb.ps1`) | PASS, `0 errors, 0 warnings`, ~267 с |
| `dm.exe smoke.dme` из `tests/byond-world` | PASS, `0 errors, 0 warnings` |
| `scripts/test-byond-world.ps1`, `test-bluemoon-unit.ps1`, `test-byond-bridge.ps1` | **не выполнено** — нет моста и тулчейна |

### N1. Домен NETRUN — выполнено

Новый проект `src/NetRun.Core` (зависит только от `NetDM.Core`, см. ADR-0001).

| Область | Файл | Что реализовано |
|---|---|---|
| Идентичность | `Identifiers.cs` | `HostId`, `PrincipalId`, `IdentityId`, `EntityId`, `ProcessId`, `OperationId`, `HostGeneration`, `CorrelationId` |
| Время | `Time.cs` | `ISimulationClock`; `SteadySimulationClock`; `ManualSimulationClock` для детерминированных сценариев |
| Хосты | `Hosts.cs` | Компонентная модель (`Network`, `Compute`, `Memory`, `Storage`, `Security`), реестр с идемпотентной регистрацией, поколениями, `Rebind`, `Destroy`, `SetLifecycle` (HOST-001) |
| Ресурсы | `Resources.cs` | Раздельные пулы CU/RAM/Storage/Bandwidth на провайдера, атомарный резерв, идемпотентный release, lease со сроком и подтверждением, восстановление по игровому времени без двойного начисления (RES-001) |
| Права | `Security.cs` | Категории не-иерархией, grant с субъектом/целью/сервисом/сроком/источником, fencing для проверки на границе commit, делегирование без расширения, цепочка «делегатор → программа», профили `Open/Standard/Secure/Quarantine` (SEC-001, SVC-003) |
| Действия | `Actions.cs` | `ActionDescriptor` со стоимостью, cooldown, дальностью, правами и документацией; `ActionRegistry`; конвейер `Prepare → Commit/Cancel` с валидацией, резервом, проверкой fencing, состоянием операции, идемпотентностью и ограниченным окном повторов (SVC-001/002, PRIM-001, ADR-0003) |
| Эффекты | `Effects.cs` | Серверные определения с пределами силы/длительности/дальности, политики стекования `Stack/Refresh/Replace/Strongest/Independent/Exclusive`, мгновенные эффекты без хранения, удаление по источнику и по цели (PRIM-003) |
| Владелец домена | `NetRunRuntime.cs` | Composition и координация lifecycle: регистрация хоста создаёт пулы, уничтожение снимает резервы, права и эффекты и отменяет незавершённые операции; `Tick` двигает время один раз |

Проверки (`tests/NetRun.Tests`, 140 утверждений):

| Команда | Результат |
|---|---|
| `dotnet build src/NetRun.Core -c Release` | PASS, 0 предупреждений, 0 ошибок |
| `dotnet build tests/NetRun.Tests -c Release` | PASS, 0 предупреждений, 0 ошибок |
| `dotnet run --project tests/NetRun.Tests -c Release` | PASS, `All NetRun.Core checks passed (140 assertions).` |

Покрытые инварианты: идемпотентность регистрации и конфликт декларации; fencing устаревшего поколения; каскад при уничтожении (резервы, права, эффекты, операции); соревнование двух потребителей за одну ёмкость; отсутствие отрицательного остатка; раздельность пулов; однократное начисление восстановления и потолок ёмкости; истечение и подтверждение lease; повторный release без второго начисления; отказ выше предела силы/длительности эффекта; невозможность применить незарегистрированный эффект; cooldown по исполнителю и группе (копия программы его не сбрасывает); идемпотентность запроса и конфликт содержимого; отмена и истечение подготовки с возвратом резерва; блокировка commit отозванным правом; `UnknownOutcome` за пределами окна идемпотентности.

Интеграция в `scripts/check-all.ps1` добавлена как проверка `NETRUN domain tests` в группе `host`.

## 3. Что не сделано и почему

| Пункт | Состояние | Условие |
|---|---|---|
| Подключение домена к процессу Host и к IPC (`netrun_*`) | не начато | этап N2 |
| Первое сквозное действие до штатного обработчика двери | не начато | этап N3 |
| Клиент NETRUN с боковыми вкладками и картой трафика (UI-001…003) | не начато; сегодня есть `NetdmSpace` без вкладок и административный `NetdmTopology` | этап N4 |
| Общий IR и bounded VM (SCRIPT-001/002) | не начато; есть только текстовый shell в `NetDM.Services` | этап N5 |
| Breach, наблюдение, ICE (§9) | не начато | этап N6 |
| Внешний мир и генерация локаций (§14) | не начато | этап N7 |
| Цифровая жизнь: ядро, миграция, снимки (§13) | не начато | этап N8 |

## 4. Технический долг и нарушения, найденные аудитом

Ссылки на файлы и строки — в [t0-audit.md](t0-audit.md) и [migration-matrix.md](migration-matrix.md). Здесь только то, что нужно закрыть, и в каком этапе.

| # | Проблема | Требование | Этап |
|---|---|---|---|
| D-01 | При выключенном netDM речь (`MOLOT:code/modules/netdm/radio_uplink.dm:263-270`) и PDA-сообщения (`messenger_relay.dm:506-515`) не доходят, кнопки и двери не работают (`code/modules/assembly/doorcontrol.dm:41-50`) | INT-002 | N3 |
| D-02 | Прямая запись в техуэб в обход леджера: `modular_bluemoon/white_steel/.../research_disks.dm:11` | INT-RD-001 | N3 |
| D-03 | `ui_data` отдаёт всем окнам глобальный журнал инцидентов и население сети (`network_space.dm:1234`, `netdm_observe.dm:858-863`) | UI-003, AT-12 | N4 |
| D-04 | Погружение не снимается при остановке подсистемы (`netdm_observe.dm:198-219`) | §13 | N4 |
| D-05 | `inspect()` диагностики недостижим из игры | §16.2 | N4 |
| D-06 | Неограниченные структуры NETDM: `SharedMediumNetwork._airtime`, `NetworkRuntime._traffic`, `ServiceRuntime._endpointGenerations`, `_scans` | ARCH-004 | отдельный этап N2 |
| D-07 | `IServiceContractModule` не подключён, контракты SS13 зашиты в `SchemaRegistry.CreateDefault` | ARCH-002 | N3 |
| D-08 | Устаревший список `scripts/bluemoon-netdm-tests.txt` (5 из 23 путей не существуют) | §21.1 | N2 |
| D-09 | Смешанная таксономия ошибок IPC (`ex.Message` рядом с `snake_case`) | §6.4 | N2 |
| D-10 | Бенчмарки в production-бинаре и на HTTP-поверхности | §18.3 | N2 |

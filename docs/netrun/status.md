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

### N2. Домен в процессе Host — выполнено

| Компонент | Файл | Что делает |
|---|---|---|
| Владелец домена в Host | `src/NetDM.Host/Transport/NetRunHostService.cs` | Один `NetRunRuntime` на процесс; единственный `lock` на домен (ни разу не удерживается через `await`); тик каждые 100 мс по тому же кванту, что и транспорт (`GameNetworkService.CurrentTick × 100`); стабильное сопоставление имени из мира в `PrincipalId`; проекция `NetRunStateView` |
| Control plane | `src/NetDM.Host/Transport/NetRunIpcModule.cs` | `IHostIpcModule` с операциями `netrun_principal`, `netrun_host`, `netrun_destroy`, `netrun_lifecycle`, `netrun_grant`, `netrun_revoke`, `netrun_actions`, `netrun_execute`, `netrun_state`; валидация размеров, перечислений, ссылок; коды статусов в snake_case |
| Первые реальные действия | `src/NetRun.Core/BuiltinActions.cs` | `net.inspect` (чтение компонентов, ресурсов и эффектов цели) и `net.scan` (обнаружение только тех хостов, которые субъект вправе обнаружить, и только в своём домене). Результат — данные авторитетного состояния, а не заглушка |
| Composition root | `src/NetDM.Host/Program.cs` | Модуль и hosted service зарегистрированы рядом с существующими; проект `NetRun.Core` добавлен в ссылки `NetDM.Host` |

Проверки на живом Host (`tests/host-ipc.ps1`, новый блок NETRUN, 20 утверждений):

| Проверка | Результат |
|---|---|
| `netrun_principal` идемпотентен по имени, разные имена — разные субъекты | PASS |
| Декларация хоста, повтор (duplicate) и конфликт декларации | PASS |
| Каталог действий приходит из одного реестра (`net.inspect`, `net.scan`) | PASS |
| Действие без capability — `forbidden` | PASS |
| Выдача трёх capability и выполнение `net.inspect` с данными авторитетного состояния | PASS |
| Повтор с тем же ключом — `duplicate` и тот же `operation` | PASS |
| `net.scan` через тот же конвейер | PASS |
| `netrun_state` показывает уменьшение буфера после затрат | PASS |
| Отзыв capability блокирует следующее действие (`forbidden`) | PASS |
| Уничтожение с устаревшим поколением — `stale_generation`; корректное — `ok` | PASS |
| Действие против уничтоженного хоста — `target_destroyed` | PASS |

Фактический прогон: `pwsh -NoProfile -File tests/host-ipc.ps1` завершился `NETDM_IPC_PASS` и кодом 0 в двух прогонах из трёх. В неудачном прогоне упало не NETRUN-утверждение, а ранее существовавшее время-зависимое (`presence answered at netshell.apc2`, в другом прогоне `shell line answered: programs | where name -eq burst-6`). Известная нестабильность зафиксирована в `docs/implementation-status.ru.md` ещё до этих изменений; набор NETRUN-утверждений проходит каждый раз, когда до него доходит исполнение.

## 3. N3. Первое действие до физического мира — частично выполнено

Реализовано и проверено:

| Что | Где | Поведение |
|---|---|---|
| Удалённые действия | `NetRun.Core/Actions.cs` (`IRemoteGameAction`, `INetRunDispatchScope`, `INetRunTransport`, `ActionStatus.Accepted`) | `Commit` удалённого действия только отправляет намерение; операция остаётся `Accepted` до отчёта цели; попадание в commit = подтверждение цели, а не отправка |
| Транспортный шов | `NetDM.Host/Transport/NetRunHostService.cs` | `INetRunTransport` поверх `GameNetworkService.Invoke`: домен не знает про NETDM, composition root передаёт транспорт; отказ допуска (`Forbidden`, `UnknownService`, `UnknownNode`, `Busy`, `IdempotencyConflict`) отображается в статус действия и освобождает резерв |
| Привязка субъекта к сети | `netrun_execute { endpoint, generation }`, `NetRunHostService.BindEndpoint` | удалённое действие идёт от имени эндпойнта оператора, поэтому транспорт и домен авторизуют одну и ту же личность |
| Завершение и неизвестный исход | `netrun_complete`, `ActionPipeline.Complete`, `ExpireRemote` | повторный отчёт не меняет ничего; ответ, не пришедший за `RemoteTimeoutMilliseconds`, становится `UnknownOutcome` и никогда не переигрывается |
| Сервисы в модели хоста | `ServicesComponent` | хост объявляет, какие контракты публикует; действие не выдумывает имя инстанса |
| Встроенные физические действия | `device.door.open`, `device.door.close` | общий cooldown `device.door`, стоимость 2 CU + 1 RAM; локальный commit запрещён — дверь меняет только её собственный обработчик |
| Порядок конвейера | `ActionPipeline.Prepare` | валидация теперь идёт до проверки cooldown, чтобы причиной отказа был реальный тупик (нет сервиса, цель уничтожена), а не «перезарядка» |

Проверки:

| Проверка | Результат |
|---|---|
| `dotnet run --project tests/NetRun.Tests -c Release` | PASS, 160 утверждений (добавлен раздел `remote actions`) |
| `dotnet run --project tests/NetDM.SmokeTests -c Release` | PASS |
| `pwsh -NoProfile -File tests/host-ipc.ps1` | PASS дважды подряд, `NETDM_IPC_PASS`, код 0 |
| `pwsh -File scripts/check-all.ps1 -Only host` | PASS целиком: Host build 6 с, Smoke tests 8 с, NETRUN domain tests 3 с, Host IPC 119 с; exit 0 |

Сценарий в `tests/host-ipc.ps1`: провайдер `nr-door` публикует `machine.door`; NETRUN-хост двери объявляет этот сервис; оператору выдаётся Execution на кибердеке и Control на двери; `netrun_execute` действия `device.door.open` проходит сеть и попадает в `delivery_poll` как команда `set_state` с `press=nr<операция>`; провайдер подтверждает `applied`; `netrun_complete` переводит операцию в `committed`; повторный отчёт даёт `invalid_state`; повторное нажатие внутри cooldown даёт `cooldown`.

Осталось по N3:

| Пункт | Почему не сделано |
|---|---|
| DM-сторона: дверь получает команду и вызывает `netrun_complete` | нужен живой мир; проверить нельзя (нет моста), поэтому не пишем непроверяемый DM-код |
| Вынос игровых контрактов в `NetRun.Content` через `IServiceContractModule` | D-07; чисто структурная работа, ждёт этап с DM-интеграцией |
| Закрытие обхода леджера (`research_disks.dm:11`) | D-02; правка DM, требует проверки в мире |
| Возврат legacy-поведения радио/PDA/кнопок при выключенном netDM | D-01; правка DM, требует проверки в мире |

## 4. Модель знания и наблюдения — выполнено

Реализовано (§9.4, PRIM-002, AT-12):

| Что | Где | Поведение |
|---|---|---|
| Уровни знания | `NetRun.Core/Knowledge.cs` | `Unknown → Anomaly → Observed → Identified`; уровни не обязательная последовательность, наблюдение только повышает знание |
| Хранилище | `KnowledgeStore` | На наблюдателя, ограничено (`MaxPerObserver = 1024`), вытесняет самую старую запись; старение по игровому времени: `StaleAfter = 60 с`, `ForgetAfter = 600 с` |
| Свежесть | `Holds(..., requireFresh)` | Устаревшая запись остаётся знанием, но не даёт права действовать; проекция показывает `stale` явно |
| Забывание | `Forget(host)`, `ForgetObserver`, `Prune` | Уничтожение хоста стирает знание о нём; сессия наблюдателя стирает всё его знание |
| Запись знания | `net.scan` пишет `Observed` для достигнутых целей; `net.inspect` подтверждает запись | Знание начинается с действия, а не с факта существования объекта |
| Гейт восприятия | `net.inspect` | Цель, которую наблюдатель не знает, отвечает как несуществующая; свой хост осмотра не требует |
| Проекция знания | `netrun_knowledge` | `host`, `level`, `confidence`, `ageMs`, `stale` — история наблюдений без имён |
| Проекция мира для клиента | `netrun_view` | только хосты, о которых наблюдатель что-то знает; имя появляется лишь при `Identified`, при `Observed` виден только класс; `stale` и `available` показаны явно. `netrun_state` остаётся инструментом разработчика и в клиент не идёт |
| Установление личности | `net.inspect` | успешный осмотр поднимает запись до `Identified`; повторный `net.scan` её не понижает |

Единый ответ на неизвестную цель: конвейер больше не отказывает по отсутствию цели до валидации. Порядок стал
`право на исполнение → идемпотентность → право на цель → валидация → cooldown → резерв`. Это убирает разницу между «объекта нет» и «объект скрыт от тебя» на уровне восприятия и сохраняет `TargetDestroyed` для честно уничтоженных целей физических действий.

Проверки:

| Команда | Результат |
|---|---|
| `dotnet run --project tests/NetRun.Tests -c Release` | PASS, **185 утверждений** |
| `pwsh -NoProfile -File tests/host-ipc.ps1` | PASS три раза подряд; проверяет, что осмотренный хост показывается с именем, а только что просканированный — без имени и с уровнем `observed` |
| `pwsh -File scripts/check-all.ps1 -Only host` | PASS целиком дважды; в третьем прогоне упало ранее существовавшее shell-утверждение (`shell line answered: $a = self $b | count`), а не NETRUN |

Нестабильность `host-ipc.ps1` — известная и ранее существовавшая: в разных прогонах падают разные shell- и presence-утверждения (`presence answered at netshell.apc2`, `shell program burst-6`, `run answered at netshell.apc3`, `shell line answered: $a = self $b | count`). Наборы NETRUN проходят каждый раз, когда до них доходит исполнение. Это записано как технический долг, а не как пройденная проверка.

## 5. ARCH-004: ограничение роста структур — выполнено

Аудит показал три структуры без верхней границы и одну, которая растёт по замыслу. Закрыто:

| Структура | Было | Стало | Проверка |
|---|---|---|---|
| `SharedMediumNetwork._airtime` | запись на каждый `(channel, z)`, когда-либо использованный; записи не удалялись | таблица описывает только текущий тик: при переходе на новый тик очищается; верхняя граница `MaxAirtimeDomains = 4096`, сверх неё — `ChannelBusy` | `AirtimeBoundsChecks`: 8 каналов за тик → 8 слотов; новый тик → 1; передача на старый тик отвергается и считается в `RejectedOldTick` |
| `NetworkRuntime._traffic` | сэмплер 1 из 16 добавлял запись на узел и не ограничивался | при достижении `MaxTrafficEntries = 4096` сэмплер начинает заново | существующие проверки маршрутизации и метрик |
| `ServiceRuntime._scans` | memo сканирований жил весь раунд и расходовал квоту операций | запись живёт `ScanRetentionTicks = 300` (30 с игрового времени); после окна тот же ключ сканирует заново | `ServiceSmokeTests`: `a scan key retires after its retention window` |
| `ServiceRuntime._endpointGenerations` | растёт по числу различных id эндпойнтов | **остаётся намеренно**: это защита от повторного использования идентификатора, размер ограничен `MaxEndpoints` | комментарий в коде и эта запись |

Проверки: `dotnet run --project tests/NetDM.SmokeTests -c Release` — PASS (`Airtime table bounds passed.`); `pwsh -File scripts/check-all.ps1 -Only host` — PASS целиком (Host build 3 с, Smoke 6 с, NETRUN domain 2 с, Host IPC 121 с).

## 6. Точка расширения контрактов — механизм сделан реальным (D-07, частично)

`SchemaRegistry.CreateDefault` больше не содержит привилегированного списка: встроенные контракты зарегистрированы теми же `IServiceContractModule`, что и любой игровой модуль (`SchemaRegistry.BuiltinContractModules`, 10 модулей). Новый сервис добавляется регистрацией модуля в composition root, а не правкой реестра.

Проверки: `ServiceSmokeTests` — «every built-in domain is a module», «the built-in modules register the stable protocol on their own» (`machine.door/set_state`, `atmos.sample`, `network.shell/run`), «duplicate feature contracts must be rejected, not overwritten».

Осталось: перенос самих игровых контрактов SS13 из `NetDM.Services/Contracts` в `NetRun.Content` — это меняет размещение модулей, а не механизм, и делается вместе с DM-интеграцией.

## 7. Что не сделано и почему

| Пункт | Состояние | Условие |
|---|---|---|
| DM-сторона N3: дверь получает команду и вызывает `netrun_complete` | не начато | нужен живой мир |
| Клиент NETRUN с боковыми вкладками и картой трафика (UI-001…003) | не начато; сегодня есть `NetdmSpace` без вкладок и административный `NetdmTopology` | этап N4 |
| Общий IR и bounded VM (SCRIPT-001/002) | не начато; есть только текстовый shell в `NetDM.Services` | этап N5 |
| Breach, наблюдение, ICE (§9) | не начато | этап N6 |
| Внешний мир и генерация локаций (§14) | не начато | этап N7 |
| Цифровая жизнь: ядро, миграция, снимки (§13) | не начато | этап N8 |
| DM-адаптер NETRUN (объявление хостов из мира, клиентские команды) | не начато | этап N3/N4 |
| Время NETRUN = квант транспорта, а не время мира из DM | осознанно | заменить на время, которое присылает DM, при интеграции N3 |

## 8. Технический долг и нарушения, найденные аудитом

Ссылки на файлы и строки — в [t0-audit.md](t0-audit.md) и [migration-matrix.md](migration-matrix.md). Здесь только то, что нужно закрыть, и в каком этапе.

| # | Проблема | Требование | Этап |
|---|---|---|---|
| D-01 | При выключенном netDM речь (`MOLOT:code/modules/netdm/radio_uplink.dm:263-270`) и PDA-сообщения (`messenger_relay.dm:506-515`) не доходят, кнопки и двери не работают (`code/modules/assembly/doorcontrol.dm:41-50`) | INT-002 | N3 |
| D-02 | Прямая запись в техуэб в обход леджера: `modular_bluemoon/white_steel/.../research_disks.dm:11` | INT-RD-001 | N3 |
| D-03 | `ui_data` отдаёт всем окнам глобальный журнал инцидентов и население сети (`network_space.dm:1234`, `netdm_observe.dm:858-863`) | UI-003, AT-12 | N4 |
| D-04 | Погружение не снимается при остановке подсистемы (`netdm_observe.dm:198-219`) | §13 | N4 |
| D-05 | `inspect()` диагностики недостижим из игры | §16.2 | N4 |
| D-06 | Неограниченные структуры NETDM | ARCH-004 | **закрыто** (см. §5); `_endpointGenerations` остаётся намеренно — см. там же |
| D-07 | Контракты SS13 зашиты в `SchemaRegistry.CreateDefault` | ARCH-002 | **механизм закрыт** (§6); перенос контрактов в `NetRun.Content` — вместе с DM-интеграцией |
| D-08 | Устаревший список `scripts/bluemoon-netdm-tests.txt` (5 из 23 путей не существуют) | §21.1 | N2 |
| D-09 | Смешанная таксономия ошибок IPC (`ex.Message` рядом с `snake_case`) | §6.4 | N2 |
| D-10 | Бенчмарки в production-бинаре и на HTTP-поверхности | §18.3 | N2 |

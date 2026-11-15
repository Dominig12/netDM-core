# Как тестировать netDM и BlueMoon

Справочник команд для проверки на любой Windows-машине: автоматические проверки, стенд с живой игрой, ручные сценарии в клиенте BYOND, нагрузка и диагностика. Все команды выполняются из корня `netDM-core` в PowerShell 7 (`pwsh`). Рядом должен лежать checkout игры `..\MOLOT-BlueMoon-Station` на ветке `net-feature-dmcore-new`.

Состояние на 14.11.2026. Проверено на Windows 10. На Linux и в Docker эти команды не проверялись.

## 1. Подготовка машины

Нужны git, .NET SDK 10, Node.js 24 (подойдут 20.19+ или 22.12+), CMake с Visual Studio C++ x86 и BYOND 516 в `C:\Program Files (x86)\BYOND`.

```powershell
git -C . pull
git -C ..\MOLOT-BlueMoon-Station pull
pwsh -File scripts\setup-bluemoon-stand.ps1
```

`setup-bluemoon-stand.ps1` выполняет четыре шага:

1. Проверяет инструменты.
2. Собирает мост `TestResults\p3-bridge\netdm_byond.dll`.
3. Экспортирует игру в `TestResults\p1\game-src`, если этой папки ещё нет.
4. Ставит пакеты TGUI.

Если в игре появились новые файлы вне списка `deploy/bluemoon/Export-Source.ps1`, удалите `TestResults\p1\game-src` и запустите setup снова.

Мост можно собрать без Visual Studio, в Docker: `pwsh -File scripts\build-bridge-mingw.ps1 -Destination TestResults\p3-bridge`.

## 2. Всё автоматическое одной командой

```powershell
pwsh -File scripts\check-all.ps1                 # host: сборка, smoke, IPC — несколько минут, BYOND не нужен
pwsh -File scripts\check-all.ps1 -Only tgui      # ESLint, tsc, Jest, сборка TGUI
pwsh -File scripts\check-all.ps1 -Only world     # DMB для тестов и 20 тестов DreamDaemon — около 15 минут
pwsh -File scripts\check-all.ps1 -Only load      # короткий замер нагрузки P10
pwsh -File scripts\check-all.ps1 -Only bridge    # мир-пример с мостом
pwsh -File scripts\check-all.ps1 -Only all       # всё подряд
pwsh -File scripts\check-all.ps1 -Only host,tgui # несколько групп через запятую
```

14.11.2026 на этой машине `-Only all` прошёл целиком: 13 проверок, около 17 минут.

После прогона печатается таблица PASS/FAIL. Логи лежат в `TestResults\check-all-<время>\`. Если хоть одна проверка упала, код выхода 1.

Группа `world` пересобирает DMB в `TestResults\p1\game-src` под профиль тестов. После неё стенд запускайте без `-NoBuild`.

## 3. Отдельные автоматические проверки

### Host (.NET)

```powershell
dotnet build src/NetDM.Host -c Release
dotnet run --project tests/NetDM.SmokeTests -c Release      # модульные проверки: shell, программы, сервисы, исследования
pwsh -File tests\host-ipc.ps1                               # живой Host по pipe: протокол, сервисы, программы, сетевое пространство
```

### Мир BlueMoon в DreamDaemon

Сначала соберите DMB для тестов. По умолчанию профиль герметичный: `CBT,CIBUILDING,LOWMEMORYMODE,UNIT_TEST_PROFILE_HERMETIC`.

```powershell
pwsh -File scripts\build-bluemoon-unit-dmb.ps1 -GameRepository ..\MOLOT-BlueMoon-Station -BuildDirectory TestResults\p1\game-src
```

Весь список netDM: 20 тестов из `scripts\bluemoon-netdm-tests.txt`.

```powershell
pwsh -File scripts\test-bluemoon-unit.ps1 -BuildDirectory TestResults\p1\game-src -BridgeDll TestResults\p3-bridge\netdm_byond.dll -TestList scripts\bluemoon-netdm-tests.txt -NetworkCameras
```

Тест считается пройденным, если в выводе:

- у каждого теста есть строка `RESULT <тест> status=0` (1 — упал, 2 — пропущен);
- есть строка `clean_run.lk: True`.

Логи мира лежат в `TestResults\p1\game-src\data\logs\netdm-p3\`, логи процессов — в `netdm-unit-dd.log` и `netdm-unit-host.log`.

Чтобы прогнать один тест, положите его путь в свой файл списка:

```powershell
Set-Content TestResults\one-test.txt '/datum/unit_test/netdm_observe/area_map/network_terminal'
pwsh -File scripts\test-bluemoon-unit.ps1 -BuildDirectory TestResults\p1\game-src -BridgeDll TestResults\p3-bridge\netdm_byond.dll -TestList TestResults\one-test.txt
```

Полные пути тестов из списка (префикс `/datum/unit_test/netdm_observe`):

| Тест | Что проверяет |
| --- | --- |
| `netdm_observe` | публикация карты и APC в Host |
| `area_map`, `area_map/node_attachment` | карта зон, подключение устройств к узлам |
| `area_map/topology_view` | данные админского окна топологии |
| `area_map/power_state` | питание APC и состояние сети |
| `area_map/network_diagnostics`, `area_map/network_terminal` | диагностика, терминал, скрипты, программы, P12 |
| `area_map/network_cameras` | камеры через сеть (только с `-NetworkCameras`, иначе тест пустой) |
| `area_map/network_air_alarm`, `area_map/atmos_telemetry` | чтение воздушной тревоги, телеметрия атмосферы |
| `area_map/messenger_shadow`, `area_map/messenger_authoritative` | мессенджер КПК |
| `area_map/radio_shadow`, `area_map/radio_uplink` | радио и подпространство |
| `area_map/door_shadow` | кнопки дверей |
| `area_map/research_shadow`, `research_authoritative`, `research_p6`, `research_authoritative/outbox` | исследования, очки, диски, outbox |
| `verb_proc_ids` | лимит ID процедур verbs |

Режимы мира переключаются ключами: `-ResearchMode authoritative`, `-MessengerMode authoritative`, `-RadioMode authoritative`. По умолчанию `shadow`; тесты authoritative включают нужный режим сами. Ключ `-NoObserve` запускает мир без netDM, это проверка обратной совместимости. `-TimeoutMinutes 40` даёт больше времени медленной машине.

### Станционные тесты (полная карта)

Тесты `atmos_station`, `door_station`, `messenger_station` и `radio_station` грузят настоящую станцию. Им нужен DMB без `LOWMEMORYMODE` и конфиг карты:

```powershell
node ..\MOLOT-BlueMoon-Station\tools\nanomaps\render.cjs --incremental     # из папки игры; один раз, картинки NanoMap
pwsh -File scripts\build-bluemoon-unit-dmb.ps1 -GameRepository ..\MOLOT-BlueMoon-Station -BuildDirectory TestResults\p1\game-src -Defines CBT,CIBUILDING,UNIT_TEST_PROFILE_HERMETIC
Set-Content TestResults\station-tests.txt '/datum/unit_test/netdm_observe/area_map/door_station','/datum/unit_test/netdm_observe/area_map/messenger_station','/datum/unit_test/netdm_observe/area_map/radio_station','/datum/unit_test/netdm_observe/area_map/atmos_station'
pwsh -File scripts\test-bluemoon-unit.ps1 -BuildDirectory TestResults\p1\game-src -BridgeDll TestResults\p3-bridge\netdm_byond.dll -TestList TestResults\station-tests.txt -Map data/netdm-audit/metastation.json -TimeoutMinutes 40
```

`render.cjs` запускайте из папки игры: `Push-Location ..\MOLOT-BlueMoon-Station; node tools\nanomaps\render.cjs --incremental; Pop-Location`.

Конфиг `data/netdm-audit/<карта>.json` создают `play-bluemoon.ps1` и аудит карт. На чистой машине сначала запустите один раз `play-bluemoon.ps1 -Map metastation` или аудит.

### Аудит станционных карт

Нужен тот же DMB, что для станционных тестов. Аудит загружает каждую карту и сохраняет отчёт: зоны без APC, изолированные сегменты, число узлов.

```powershell
pwsh -File scripts\audit-bluemoon-maps.ps1 -BuildDirectory TestResults\p1\game-src -BridgeDll TestResults\p3-bridge\netdm_byond.dll -Maps metastation,boxstation
pwsh -File scripts\audit-bluemoon-maps.ps1 -BuildDirectory TestResults\p1\game-src -BridgeDll TestResults\p3-bridge\netdm_byond.dll      # все 24 карты ротации, долго
```

Отчёты сохраняются в `TestResults\map-audit\<карта>\netdm_map_audit.json`, сводная таблица печатается в конце.

### Мост BYOND

```powershell
pwsh -File scripts\test-byond-bridge.ps1                                        # сборка моста CMake и проверка с Host
pwsh -File scripts\test-byond-world.ps1 -BridgeDll TestResults\p3-bridge\netdm_byond.dll   # маленький мир DM с мостом и Host
```

### TGUI игры

Команды выполняются в `..\MOLOT-BlueMoon-Station\tgui`. Вместо `<yarn>` подставьте `.yarn\releases\yarn-4.18.1.cjs` или ту версию, что лежит в этой папке.

```powershell
node <yarn> install
node <yarn> eslint --ext .js,.ts,.tsx packages/tgui/interfaces/NetdmTerminal.tsx packages/tgui/interfaces/NetdmSpace.tsx packages/tgui/interfaces/NetdmTopology
node <yarn> tsc --noEmit
node <yarn> jest packages/tgui/interfaces/NetdmTopology
node <yarn> run tgui:build
```

## 4. Нагрузка и производительность

```powershell
# P10: цепочка APC с устройствами и правилами, питание APC мигает с заданной частотой.
pwsh -Command "& ./tests/p10-load.ps1 -Apcs 450 -Devices 16 -Seconds 30 -Rates @(0,2,5,20) -Rules @(0,1,2,3) -Output TestResults/p10.json"
pwsh -Command "& ./tests/p10-load.ps1 -Apcs 450 -Rules @(2)"      # только трассы через карту, самое дорогое правило
```

Массив передавайте через `pwsh -Command`: `pwsh -File` склеивает его в одну строку. В таблице колонки:

- `cpu ms/s` — время CPU Host за секунду;
- `programs eval/fired` — оценка и срабатывание правил;
- `links pub`, `dir pub` — публикации портов и каталога;
- `tick lag` — задержка тика.

Ориентир на этой машине: 450 APC стоят 3–7 % одного ядра.

Профили маршрутизации на больших сетях:

```powershell
pwsh -File scripts\run-profiles.ps1 -LaunchHost                                  # 1k/10k/30k узлов, 3 seed
pwsh -File scripts\run-profiles.ps1 -LaunchHost -Sizes '1000,10000' -Seeds '1337' -Routes 200
```

Наблюдение за запущенным Host, на стенде или в тестах:

```powershell
pwsh -File scripts\observe-host.ps1 -BaseUrl http://127.0.0.1:<порт> -DurationSeconds 300   # CPU, GC, паузы, метрики HTTP
pwsh -File scripts\capture-stalls.ps1 -BaseUrl http://127.0.0.1:<порт> -Label stand -DurationSeconds 120   # с трассой GC (dotnet-trace)
pwsh -File scripts\capture-stalls.ps1 -BaseUrl http://127.0.0.1:<порт> -SkipTrace          # без трассы
```

Перед выводами о задержках проверьте `maxGapMs` в отчёте: эта машина сама замирает на 5–8 с.

## 5. Стенд для игры в клиенте BYOND

```powershell
pwsh -File scripts\play-bluemoon.ps1                                   # MetaStation, всё authoritative
pwsh -File scripts\play-bluemoon.ps1 -Mode shadow                      # старое поведение игры, netDM только наблюдает
pwsh -File scripts\play-bluemoon.ps1 -Map boxstation -NetworkCameras   # камеры через сеть
pwsh -File scripts\play-bluemoon.ps1 -Map runtime                      # маленькая отладочная станция, быстрый старт
pwsh -File scripts\play-bluemoon.ps1 -ResearchMode shadow -MessengerMode authoritative -RadioMode authoritative
pwsh -File scripts\play-bluemoon.ps1 -NoBuild                          # без пересборки: запустить то, что собрано
pwsh -File scripts\play-bluemoon.ps1 -GamePort 52525 -Minutes 60
```

Подключение: DreamSeeker → Open location → `byond://127.0.0.1:51515`. Локальный игрок получает права админа. Раунд начинается в лобби: Start Now на вкладке Server или Ready.

Остановить стенд можно тремя способами:

- `Ctrl+C`;
- создать файл: `New-Item TestResults\p1\game-src\netdm-stand.stop`;
- дождаться окончания `-Minutes`.

Логи:

- `TestResults\p1\game-src\netdm-stand-host.log`, `netdm-stand-dd.log` и их `-err`;
- логи мира в `TestResults\p1\game-src\data\logs\`.

Состояние стенда по HTTP, во втором окне:

```powershell
pwsh -File scripts\stand-status.ps1                 # один раз
pwsh -File scripts\stand-status.ps1 -Every 10       # каждые 10 с до Ctrl+C
```

Скрипт берёт адрес Host из `netdm-stand-host.log`. Тот же адрес печатает строка `STAND_READY ... host=http://127.0.0.1:<порт>`. Отдельные запросы:

```powershell
$h = 'http://127.0.0.1:<порт>'
Invoke-RestMethod "$h/api/health"
Invoke-RestMethod "$h/api/readiness/game"; Invoke-RestMethod "$h/api/readiness/network"
Invoke-RestMethod "$h/api/game/summary"
Invoke-RestMethod "$h/api/game/map?z=2&limit=200&outer=true"
Invoke-RestMethod "$h/api/research/status"
Invoke-RestMethod "$h/api/scheduler"; Invoke-RestMethod "$h/api/metrics"
Invoke-RestMethod -Method Post "$h/api/research/background/reset"     # обнулить счётчики фоновых стадий перед замером
```

Обсерватория Host открывается в браузере по адресу `http://127.0.0.1:<порт>/`.

Дополнительные переключатели задаются переменными окружения в том же окне перед запуском стенда. Они передаются процессам стенда по наследованию; на стенде это пока не проверялось.

```powershell
$env:NETDM_DOOR_MODE = 'authoritative'     # двери двигаются только по сетевой команде (решение 14.11)
$env:NetDM__ProgramStateBytes = '8192'     # бюджет $state программы, 256–65536 (по умолчанию 4096)
pwsh -File scripts\play-bluemoon.ps1
```

## 6. Ручные проверки в игре

### Где открыть терминал

- **APC:** мультитул по закрытому APC. Нужен инженерный доступ или разблокированный интерфейс APC. Иначе придёт «Доступ запрещён».
- **КПК:** держать в руке, на главном экране кнопка «Сетевой терминал».
- **Имплант:** сетевой имплант (узел исследований робототехники). Кнопка «Сетевой терминал» на панели действий.
- **Админ:** вкладка Debug → «netDM Topology» рисует всю сеть.

### Строки терминала: чтение

Строку вводят в поле терминала и нажимают «Выполнить».

```text
self                                         # свой узел: состояние, провода, радио, устройства
links                                        # соседи и состояние каждой связи
links | where state -ne up                   # только сломанные связи
services                                     # видимые сервисы сети (если много — сузить)
services -type research.server               # серверы исследований
services -type machine.door | count
services -prefix netshell -hops 2 | sort hops
services -hops 3 | select instance type hops
services type=network.shell | sort hops -desc | first 3
services | where instance -like "research.*"
route research.<id>                          # путь до сервиса: хопы, cable или radio
trace <номер узла>                           # достижимость узла: hops, radioHops, ticks
jobs                                         # свои недавние операции и их исходы
programs                                     # свои программы: state, reason, runs
```

Поля: `where <поле> -eq|-ne|-lt|-le|-gt|-ge|-like <значение>`. В `-like` символ `*` означает любую подстроку, совпадать должен весь текст. Остальные фильтры: `select <поля…>`, `sort <поле> [-desc]`, `first N`, `last N`, `count`.

Имена экземпляров:

| Префикс | Сервис |
| --- | --- |
| `netshell.<id>` | терминал |
| `netdiag.<id>` | диагностика |
| `netquery.<id>` | запросы |
| `research.<сервер>` | сервер исследований |
| `door.<id>` | дверь |
| `camera.<id>` | камера |
| `msgsrv.<id>` | сервер сообщений |
| `pda.<id>` | КПК |
| `rx.<id>` | приёмник радио |

Типы сервисов: `network.shell`, `network.diagnostics`, `network.query`, `research.server`, `machine.door`, `camera.access`, `atmos.alarm`.

Что проверить на ошибках:

- `services | sort` → подсказка синтаксиса;
- `services -hops 99` → выход за предел 0–64;
- `foo` → `unknown_command`;
- `services | where instance -like "open` → `parse_error` с позицией;
- больше двух поисков `services`/`route`/`trace` в одной строке → «network search budget exceeded»;
- на большой станции голый `services` → «too many services: narrow with -type or -prefix».

### Строки терминала: действие

```text
invoke netdiag.<id> link_state reason=check                       # одна команда сервису
services -type network.diagnostics -hops 1 | invoke link_state reason=near   # каждому из выборки
jobs | first 5                                                    # затем смотреть исходы
```

`invoke` идёт от имени того, кто спрашивает, с его правами и квотами. Строка не ждёт ответа: каждая команда сразу возвращает номер операции. `invoke` в сервис `network.shell` запрещён: `forbidden`.

### Скрипты (кнопка «Скрипт»)

```text
$labs = services -type research.server | sort hops
$near = $labs | first 1
if $near | where hops -lt 3
  $near | invoke status reason=open
end
$labs | count
```

Сервис может отказать команде терминала по своей политике. Такой отказ тоже нормальный ответ: он виден в `jobs`.

Проверить:

- переменная без значения → `invalid_argument`;
- «Пример» вставляет образец;
- «На диск», «В редактор», «Запустить с диска» работают с портативным диском в руке (файл `netdm.NDS1`).

### Программы (кнопка «Программы»)

Программа с именем запускается кнопкой «Запустить программу». «Хранить на устройстве» записывает её на носитель устройства, и она запускается при каждом подключении.

**Правило.** Первая строка `on <чтение>`. Программа выполняется, когда меняются данные, которые читает правило.

```text
on links | where state -ne up
invoke netdiag.<id> link_state reason=refresh
```

**Таймер.** `every N`, N от 10 до 3600 с. `$state` сохраняется между запусками, `publish` публикует от имени самого устройства.

```text
every 10
if $state
  $state | publish
end
$state = self | select node state
```

**Фазы.** `wait` ждёт результатов предыдущих `invoke`, `$result` хранит их исходы.

```text
invoke door.<id> set_state press=t1 state=open
wait
if $result | where outcome -eq applied
  invoke door.<id> set_state press=t2 state=closed
end
```

Проверить:

- `programs` показывает state, reason и runs;
- «Остановить» в списке программ;
- `every 2` → `invalid_argument`, таймер не меньше 10 с;
- `$state = jobs` с большим выводом → программа падает по бюджету `$state` (4096 знаков);
- больше программ на устройство, чем позволено → отказ;
- кнопка «Хранить на устройстве»: вынуть имплант или КПК, вставить снова, программа снова в `programs`;
- перенос: `migrate <номер программы> netshell.<id другого устройства>`. Программа сохраняет номер и `$state`, на старом месте её состояние становится `moved`;
- рестарт Host посреди раунда (убить `NetDM.Host`): стенд его не перезапускает. Это делает лаунчер комплекта (`Start-BlueMoon.ps1`). После его перезапуска программы раунда возвращаются из `programs-<раунд>.json`.

### Окно сети (кнопка «Сеть» в терминале)

1. «Сканировать» с радиусом 1–8 хопов. Узлы и связи появляются на карте. Повтор раньше 10 с → кнопка недоступна, Host отвечает `busy`. Через 60 с сведения помечаются «устарели».
2. «Войти»: на карте появляется ваше присутствие ○ на узле входа.
3. Выбрать узел с терминалом → «Перейти». Перемещение занимает 1 с на хоп, не больше 15 с. На узле назначения работают «Командная строка» и «Команды», журнал внизу.
4. Узел с датчиком атмосферы → «Снять показания».
5. «Выйти» завершает присутствие. Если закрыть окно, присутствие приостанавливается (`operator_away`), «Продолжить» возвращает его.

Отказы, которые стоит вызвать руками:

| Как вызвать | Что ожидать |
| --- | --- |
| Обесточить APC, на котором стоит присутствие | `suspended: host_unpowered`; с питанием снова `active` |
| Закрыть окно больше чем на 5 минут | присутствие завершено: `operator_gone` |
| Терминал входа теряет сеть (КПК в космос, обесточить APC входа) | `suspended: entry_lost`; через 60 с `entry_lost` и конец |
| Разорвать маршрут между входом и узлом присутствия, затем выполнить команду | отказ `out_of_reach` |
| Разорвать маршрут во время «Перейти» | возврат к источнику, `returned: route_lost` |
| То же, но источник тоже пропал | `lost_in_transit` |

Отказы `entry_lost`, `operator_gone`, `out_of_reach` и `lost_in_transit` пока проверены только чтением кода и тестами Host. Ручная проверка в клиенте ещё не проводилась.

### Игровые системы

**Исследования** (`-ResearchMode`).

- Консоль R&D: исследовать узел; очки сервера консоли списываются один раз.
- ПКМ по консоли → «Собрать очки на сервер»: перевод очков с другого сервера сети.
- Диск: «Download to disk», затем «Upload disk» на другой консоли. Переносятся только знания, без очков.
- Источники очков (деструктивный анализатор, экспериментатор, допплер, тесла-катушки, коллекторы, вскрытие) начисляют очки серверу-источнику.

**Мессенджер** (`-MessengerMode`).

- Два КПК: сообщение приходит один раз, «Ответ» и «Блок» работают.
- Выключить message server в серверной телекомма → «ERROR: Server is not responding.»
- КПК вдали от APC (космос, глухие техтоннели) → отправителю приходит «not delivered».

**Радио** (`-RadioMode`).

- Говорить в общий канал из разных отделов: слышат все.
- Вдали от APC гарнитура не дотягивается до телекомма; обычная рация через 2 с шлёт резервную передачу.
- Выключить приёмники телекомма → речь по подпространству никто не слышит.

**Камеры** (`-NetworkCameras`).

- Охранная консоль: выбрать камеру в другой зоне и дождаться изображения.
- Обесточить APC камеры: помехи, после возврата питания картинка возвращается.

**Двери** (`NETDM_DOOR_MODE=authoritative`).

- Кнопка дверей: двери двигаются один раз, по сетевой команде.
- Обесточить APC двери: команда не исполняется, причина в ответе.

**Воздушная тревога.** «Снять показания» в окне сети: в журнале строка с давлением (кПа), температурой (K) и пометкой «тревога в зоне», если она есть.

**Админ.** «netDM Topology»: показывает уровни, компоненты сети и состояние APC. В логе мира должны быть строки `netDM`.

## 7. Linux и комплекты

Ниже команды, которые пока не проверялись на этой машине.

```bash
tests/host-integration.sh                                   # Host на Linux: HTTP и pipe
```

```powershell
pwsh -File scripts\test-byond-world-linux.ps1 -ByondImage <образ> -NativeBuilderImage <образ>
pwsh -File deploy\bluemoon\Test-Compose.ps1 -EnvironmentFile <.env> -ProjectName netdm-check
pwsh -File deploy\bluemoon\Test-WindowsBundle.ps1 -Bundle <свежая распаковка комплекта>
```

## 8. Что приложить к сообщению об ошибке

- Команду запуска и её последние 30 строк.
- `TestResults\p1\game-src\netdm-stand-*.log` или `netdm-unit-*.log`.
- Папку лога мира из `data\logs\` (`runtime.log`, `world.log`, `unit_tests.json`).
- Вывод `pwsh -File scripts\stand-status.ps1`, если стенд был запущен.
- Версии: `git -C . rev-parse --short HEAD`, `git -C ..\MOLOT-BlueMoon-Station rev-parse --short HEAD`, `node --version`, `dotnet --version`.

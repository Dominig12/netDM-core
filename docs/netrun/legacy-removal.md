# Снятие легаси: очередь и учёт

Правило — [ADR-0007](adr/0007-legacy-removal.md). Каждая строка: что снимается, почему это легаси по документу, что проверено после удаления.

## Сделано

| Что | Где | Требование | Проверка |
|---|---|---|---|
| Четыре несуществующих пути в списке DM-тестов (`messenger_shadow`, `radio_shadow`, `door_shadow`, `research_shadow`) | `netDM-core/scripts/bluemoon-netdm-tests.txt` | §21.1: список проверок должен запускать реальные тесты | живой прогон: тесты находятся и проходят |
| Дублирующие `null`-поля в запросе персоны | `MOLOT:code/modules/netdm/network_space.dm` | §6.4: ошибка не должна быть тишиной | живой тест `network_immersion_dive` — PASS |
| Заглушка `set_authoritative()` у дверей (мёртвый код, вызовов нет) | `MOLOT:code/modules/netdm/door_control_shadow.dm` | ADR-0007: иллюзия переключаемости | DM-сборка 0/0; живой прогон `network_airlock_panel`, `netrun_client`, `netrun_adapter` — PASS, `clean_run.lk: True` |

## Сделано в этом заходе

| # | Что снято | Доказательство | Проверка |
|---|---|---|---|
| L-2 | `authoritative_station()` (76 строк) — helper, чей тип `radio_station` не имеет своего `Run()` и наследует аудит карты; запись удалена из списка тестов | функция не вызывалась ниоткуда, а документация ссылалась на неё как на проверку авторитетного радио | живой прогон: список грузится без `Unknown test` |
| L-5 (первая половина) | `NETDM_RADIO_MODE`, поле `authoritative`, `set_authoritative()` | при дефолтном `shadow` `rx_instances` пуст, `uplink()` возвращал TRUE без доставки, и `send_to_receivers` не запускал legacy — **речь пропадала молча** | `radio_uplink` PASS: `receivers_offered=5 uplinks=3 sent=6 relayed=1 heard=1`, без единого вызова `set_authoritative` |
| L-5 (поведение) | `no_receiver` больше не выдаёт себя за успех | при отсутствии сетевого приёмника передача возвращается физической цепи, а не исчезает | тот же прогон, `no_receiver=0` |

Герметичная DM-сборка: 0 ошибок, 0 предупреждений, `UNIT_DMB_READY`. Живой прогон `radio_uplink`, `messenger_authoritative`, `netrun_client` — PASS, `clean_run.lk: True`.

| L-5 (вторая половина) | Теневая выборка `ask`/`answered`/`pending`/`latencies` (4 запроса в секунду к Host), C#-`RadioReach.cs` (107 строк), регистрация `radio_reach`, ветка `radio_` в `send_control` и `bridge.radio()` | после снятия режима у выборки не осталось второй стороны для сравнения, а запросы продолжали уходить | `radio_uplink` PASS: диагностика теперь `receivers_offered=5 uplinks=3 sent=6 relayed=1 heard=1`, без счётчиков выборки |

Итого L-5 закрыт: режим снят, затем снята выборка и её C#-опора. Постоянная нагрузка от теневого пилота на BYOND и на control-очередь прекратилась.
## Очередь снятия (проверено чтением обоих репозиториев, 25.11.2026)

Порядок — от безопасного к рискованному. Везде проверка по ADR-0007 §3.

| # | Что снимается | Тип | Доказательство легаси | Объём | Риск | Проверка |
|---|---|---|---|---|---|---|
| L-1 | Поле `authoritative`, ветка «shadow» и `set_authoritative` в `door_control_shadow.dm`; переименовать файл в `door_control.dm` | М | присваиваний `authoritative` нет ни одного, ветка `if(!record["authoritative"])` недостижима | ~10 строк | низкий | DM-сборка; `netdm_door_dispatch`, `door_station`, `network_airlock_panel` |
| L-2 | `authoritative_station()` в `unit_tests/netdm_observe.dm:1467-1542` | М/фиктивный тест | у `radio_station` **нет своего `Run()`** — он наследует аудит карты; `authoritative_station` не вызывается никем, а `status.md` ссылается на него как на проверку P8 | 76 строк тестов | низкий | список тестов грузится без `Unknown test` |
| L-3 | Сравнительный слой `atmos_telemetry.dm` (отчёты, `compare`, `explain`, латентности, `legacy_received`) | П+М | `legacy_received` не вызывается, `receive_signal` консоли — no-op, `report["legacy"]` всегда пуст | ≈200 из 553 | средний | DM-сборка; `atmos_telemetry`, `network_air_alarm` |
| L-4 | Legacy-сравнение `messenger_shadow.dm` (sends/operations/compare/explain/state) | П | телеграфного пути больше нет: `send_to_receivers` не вызывает `..()` | ≈170 из 341 | средний | `messenger_authoritative` (убрать `set_authoritative(TRUE)` в тесте) |
| L-5 | Shadow-выборка радио (`radio_shadow.ask/answered/legacy_broadcast`), `NETDM_RADIO_MODE`, `set_authoritative`, C#-`radio_reach` (107 строк + регистрация) | П+М | при `RadioMode=shadow` `rx_instances` пуст → `uplink()` возвращает TRUE без доставки → **речь пропадает молча** | ≈150 DM + 117 C# | средне-высокий | `radio_uplink`; живой раунд без env-флага: `uplink_counters["relayed"] > 0` |
| L-6 | Зеркало техуэба в `research_shadow.dm`: `console_purchased`/`waiting`/`send_purchases` (ноль вызовов), `compared`, `mirror_lost`, `legacy_*` и безусловные записи в леджер | П+М+Д | половина зеркала (покупки) мертва с переходом на `console_research`; `legacy_boost` пишет в леджер без `owns()`, пока `projected()` пишет обратно | ≈250–300 из 1679 | высокий | `research_authoritative`, `research_p6`, `.../outbox`; ручная проверка консоли и дисков |
| L-7 | Дублирующий сид населения (`netdm_observe.dm:662-663`) и устаревшие комментарии «in Shadow mode» | М | второй блок в том же тике не может сработать раньше первого | ~8 строк | низкий | `network_life_physical`, `map_audit` |
| **L-8 — снято** | Бенчмарки из production-хоста (`Benchmarks/*`, 1971 строка; маршруты `/api/benchmark*`, `/api/comparison*`, `/api/history`, `/api/packets`; `/api/map` через `benchmark.ActiveSharedMedium`) | П | `Program.cs` уже прячет их при `NetDM:Deployment` — значит они не часть игры; транспорт их не использует | 1971 C# + ~120 в `Program.cs` | средний по игре, высокий по CI | [ADR-0008](adr/0008-benchmarks-outside-host.md) выполнен по шагам: L8-1 — библиотека `NetDM.Benchmarks`; L8-2 — отдельный `NetDM.BenchmarkHost` на своём порту (проверен по HTTP и профильным скриптом); L8-3 — игровой хост без маршрутов, DI и ссылки (живой хост: `/api/metrics` и `/api/map` — 200, `/api/benchmark` — 404, `NetDM.Benchmarks.dll` в выходе нет); L8-4 — `host-integration.sh` и три workflow собирают и запускают оба процесса. **Оговорка:** CI не запускался локально (WSL без дистрибутива), поэтому интеграционный шаг и workflows считаются непроверенными до первого прогона на GitHub. |

## Фиктивные переключатели — снять или сделать конфигом раунда

| Переменная | Состояние |
|---|---|
| `NETDM_RESEARCH_MODE` | **снят (L-6)**: код его не читал, власть задана безусловно; параметры `-ResearchMode` удалены из трёх скриптов |
| `NETDM_MESSENGER_MODE` | **снят (L-4)**: код его не читал; параметры `-MessengerMode` удалены |
| `NETDM_RADIO_MODE` | **снят (L-5)**: режим убран вместе с теневым пилотом; параметры `-RadioMode` удалены |
| `NETDM_CAMERA_MODE`, `NETDM_NET_IMMERSION`, `NETDM_NET_LIFE` | **реальные механизмы**, но за env-флагом; по документу должны быть конфигом раунда, а не переменной окружения |

## Дублирующий авторитет (отдельные этапы, не входят в L-1…L-8)

1. **Двери:** `machine.door/set_state` → `netdm_apply_controls` пишет `locked`/`aiDisabledIdScanner`/`secondsElectrified`/`safe` напрямую, а `machine.door/control` → `apply_panel_action` делает то же через штатные проверки. Оставить один путь — штатный, дубль удалить.
2. **Research:** `legacy_unlock`/`legacy_boost` пишут в леджер после перехода, `project_node`/`projected` — обратно в техуэб.

## Не снимать

`legacy_activate` кнопок и legacy-путь радио (INT-002), `topology_view`/`NetdmTopology` (диагностика), текстовый shell и `network.query` (инструменты игрока), `NETDM_NET_LIFE`/`NETDM_NET_IMMERSION` (реальные механизмы), `DynamicRadioNetwork`/`CompactRouter`/`BidirectionalRouter` (оракулы эквивалентности в smoke-тестах), весь код NETRUN.
## L-4: попытка и откат (26.11.2026)

`messenger_shadow.dm` переписан с 341 до ~150 строк: убрана мёртвая половина (сравнение доставки, `sends`/`operations`/`latencies`/`counters`), оставлены реестр мессенджеров, ретрансляция, дедуп показа и диагностика. Сборка — 0 ошибок, но живой тест `messenger_authoritative` начал устойчиво регистрировать **61 из 62** эндпойнтов (`unsettled=1`, `poll_gap ≈ 1 с`).

Правка откачена, тест снова PASS (46.7 с, 62/62). Вывод: в «мёртвой» половине есть неявная зависимость регистрации; снимать её можно только после разбора `add_messenger`/`add_server` и порядка регистрации, а не переписыванием файла целиком.

Побочная польза: `messenger_program.dm` отсутствовал в списке обновления герметичной сборки, поэтому правки в нём в неё не попадали. Файл добавлен.
## L-4 закрыт: сравнение мессенджера снято (26.11.2026)

Метод: не переписывание файла, а последовательные резы с живым прогоном после каждого.

| Шаг | Что снято | Результат `messenger_authoritative` |
|---|---|---|
| A | `compare`, `explain`, `explained`, `legacy_received`, цикл ожидания в `tick` | PASS |
| B | `end_send`: мёртвая ветка зеркалирования после `if(authoritative) return` (≈35 строк, недостижима) | PASS |
| C | `delivered`/`receipt` сведены к живым путям; `accepted`, `operations`, `sends`, `latencies`, `counters`, `exceptions`, `notes`, `note()`, легаси-счётчики в `state()` | PASS |

Итог: файл сократился с 341 до ~159 строк, `state()` показывает только реальные числа (`pdas`, `arrival_p50/p95`, `reasons`, `relay_state`). Живой прогон стал короче: 41.4 с против 46–48 с до снятия, `clean_run.lk: True`.

Урок: прошлая неудача была не в «неявной зависимости», а в том, что я резал файл цельным переписыванием и не проверял промежуточные состояния. Пошаговый рез с прогоном после каждого шага проходит без потерь.
## L-3 закрыт: сравнение атмосферы снято (26.11.2026)

Тот же пошаговый метод: рез → живой прогон → следующий рез.

| Шаг | Что снято | Проверка |
|---|---|---|
| A | `legacy_received`, `console_key` — приём радиодоклада для сравнения | PASS |
| B | `compare`, `explain`, `why_not_recipient`, `console_stats`, `note` | PASS |
| C | цикл сравнения в `tick`, `note(...)` в приёме, `per_console`, `reason_cache`, `mismatches`, `snapshot_latencies` | PASS |
| D | `state()` сведён к реальным числам: отчёты, снимки, unmirrored, задержки доставки | PASS |

Файл: **553 → 421 строка** (`−141/+9`). Полный живой набор после снятия: **23 из 24 PASS**, единственное падение — `door_station` (в герметичном мире нет кнопок blast-дверей).

Попутно восстановлены `netdm_atmos_instance` и `netdm_tick_of`, случайно попавшие под рез: они не относились к сравнению и нужны доставке. Это тот же урок, что и с мессенджером — резать надо по одному шагу и сразу проверять.

Очередь снятия легаси закрыта.

## L-6 закрыт: у зеркала техуэба один писатель (26.11.2026)

Очередь называла L-6 «снять зеркало». Аудит кода перед резом показал обратное: само зеркало
(seed, income, grant, debit, import, boost) нужно — без него `authorize()` спроецировал бы пустой
реестр на веб и стёр бы его очки. Легаси оказалось не зеркало, а **сверка** зеркала с вебом и
**второй путь покупки**.

| Шаг | Что снято | Проверка |
|---|---|---|
| A | `console_purchased`, `send_purchases`, список `waiting` и барьер `confirmed_seq` — второй путь покупки, у которого не осталось вызовов: `research_node` консоли всегда идёт через `console_research` | сборка 0/0; `research_authoritative`, `research_p6`, `.../outbox` — PASS |
| B | `compared()` — сверка баланса и знаний с вебом; `mismatch()` с выравниванием (`research_import` + `research_debit`), `mismatch_reasons`, `last_drift`/`max_drift`, `NETDM_RESEARCH_COMPARE` | те же три — PASS |
| C | Поле `authoritative` у покупки и теневые ветки в `purchase_answered`, `purchase_accepted`, `expire_purchases`, `reset_session` | те же три — PASS |

Исход эффекта «shadow» стал «foreign»: эффект покупки не этого раунда, применять нечего.
`state()` показывает только реальные числа; ответы леджера считаются в `purchase_outcomes`
(тест `research_authoritative` читает оттуда отказ `insufficient_points`).

Побочно сняты фиктивные переключатели: `NETDM_RESEARCH_MODE`, `NETDM_MESSENGER_MODE`,
`NETDM_RADIO_MODE` не читались кодом, поэтому параметры `-ResearchMode/-MessengerMode/-RadioMode`
убраны из `play-bluemoon.ps1`, `start-bluemoon-stand.ps1` и `test-bluemoon-unit.ps1`, а подсказка
стенда больше их не обещает. `play-bluemoon.ps1` потерял и зонтичный `-Mode`.

Файл: **1680 → 1553 строки** (`research_shadow.dm`, −175/+48 в диффе этого захода).

## L-7 закрыт: дублирующий сид и устаревшие комментарии (26.11.2026)

| Что | Где | Почему | Проверка |
|---|---|---|---|
| Второй сид населения в том же тике | `netdm_observe.dm`, `publish_ack` | первый блок срабатывает раньше в том же вызове, второй не может добавить ничего | `network_life_physical` — PASS |
| Описания «in Shadow mode» и сравнения, которого больше нет | доки полей `telemetry/research/messenger/radio/doors`, шапка `atmos_telemetry.dm` | документация обещала снятое | — |
| Мёртвые счётчики сверки (`compared`, `matched`, `legacy_only`, `netdm_only`), `reasons`, `report["legacy"]`, неиспользуемый `NETDM_TELEMETRY_SETTLE` | `atmos_telemetry.dm` | учёт без второй стороны | `atmos_telemetry`, `network_air_alarm` — PASS |

Заодно задержка доставки стала считаться: `push(latencies, ...)` на принятом отчёте, а
`unmirrored` инкрементируется там, где отчёт не удалось опубликовать. Раньше `delivery_p50/p95`
всегда печатали `-`, а счётчик не рос.

Полный живой набор после L-6 и L-7: **23 из 24 PASS**, единственное падение — `door_station`
(«The station needs door buttons: 2»): в герметичном мире нет кнопок blast-дверей. Это
ограничение профиля, а не регрессия; из-за него `clean_run.lk` полного набора — `False`.

# Снятие легаси: очередь и учёт

Правило — [ADR-0007](adr/0007-legacy-removal.md). Каждая строка: что снимается, почему это легаси по документу, что проверено после удаления.

## Сделано

| Что | Где | Требование | Проверка |
|---|---|---|---|
| Четыре несуществующих пути в списке DM-тестов (`messenger_shadow`, `radio_shadow`, `door_shadow`, `research_shadow`) | `netDM-core/scripts/bluemoon-netdm-tests.txt` | §21.1: список проверок должен запускать реальные тесты | живой прогон: тесты находятся и проходят |
| Дублирующие `null`-поля в запросе персоны | `MOLOT:code/modules/netdm/network_space.dm` | §6.4: ошибка не должна быть тишиной | живой тест `network_immersion_dive` — PASS |
| Заглушка `set_authoritative()` у дверей (мёртвый код, вызовов нет) | `MOLOT:code/modules/netdm/door_control_shadow.dm` | ADR-0007: иллюзия переключаемости | DM-сборка 0/0; живой прогон `network_airlock_panel`, `netrun_client`, `netrun_adapter` — PASS, `clean_run.lk: True` |

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
| L-8 | Бенчмарки из production-хоста (`Benchmarks/*`, 1971 строка; маршруты `/api/benchmark*`, `/api/comparison*`, `/api/history`, `/api/packets`; `/api/map` через `benchmark.ActiveSharedMedium`) | П | `Program.cs` уже прячет их при `NetDM:Deployment` — значит они не часть игры; транспорт их не использует | 1971 C# + ~120 в `Program.cs` | средний по игре, высокий по CI | `dotnet build`, smoke, `host-integration.sh` после переноса в `NetDM.Benchmarks` |

## Фиктивные переключатели — снять или сделать конфигом раунда

| Переменная | Состояние |
|---|---|
| `NETDM_RESEARCH_MODE` | **нигде не читается**, включение безусловное (`research_shadow.dm:159`); флаг в скриптах вводит в заблуждение |
| `NETDM_MESSENGER_MODE` | **нигде не читается**; `authoritative = TRUE` жёстко, `set_authoritative(FALSE)` возвращает FALSE |
| `NETDM_RADIO_MODE` | читается, но дефолт `shadow` ломает речь → снимается вместе с L-5 |
| `NETDM_CAMERA_MODE`, `NETDM_NET_IMMERSION`, `NETDM_NET_LIFE` | **реальные механизмы**, но за env-флагом; по документу должны быть конфигом раунда, а не переменной окружения |

## Дублирующий авторитет (отдельные этапы, не входят в L-1…L-8)

1. **Двери:** `machine.door/set_state` → `netdm_apply_controls` пишет `locked`/`aiDisabledIdScanner`/`secondsElectrified`/`safe` напрямую, а `machine.door/control` → `apply_panel_action` делает то же через штатные проверки. Оставить один путь — штатный, дубль удалить.
2. **Research:** `legacy_unlock`/`legacy_boost` пишут в леджер после перехода, `project_node`/`projected` — обратно в техуэб.

## Не снимать

`legacy_activate` кнопок и legacy-путь радио (INT-002), `topology_view`/`NetdmTopology` (диагностика), текстовый shell и `network.query` (инструменты игрока), `NETDM_NET_LIFE`/`NETDM_NET_IMMERSION` (реальные механизмы), `DynamicRadioNetwork`/`CompactRouter`/`BidirectionalRouter` (оракулы эквивалентности в smoke-тестах), весь код NETRUN.
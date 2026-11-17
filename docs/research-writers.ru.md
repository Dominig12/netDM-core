# Legacy research writers BlueMoon

Инвентаризация для P0 и search gate P5. База: `MOLOT-BlueMoon-Station@4928bc0a57` (`net-feature-dmcore-new`), рабочее дерево 10.11.2026. Проверены все `*.dm` в `code/`, `modular_bluemoon/`, `modular_citadel/`, `modular_sand/`, `modular_splurt/`. В `citadel/` и `goon/` файлов `.dm` нет. Новые файлы netDM Observe research не затрагивают. `_maps` не проверялись: var-edits `network_id` и предметы на картах нужно сверить отдельно.

Обозначения: **D** — прямая запись поля, **I** — через proc techweb. Номера строк относятся к указанной базе. Выборочно сверены строки 45 `discoverable_component.dm`, 86–94 `_techweb_node.dm`, 183–208 и 374–396 `_techweb.dm`.

## Примитивы записи

Все в `code/modules/research/techweb/_techweb.dm`, если не указано иное.

| Примитив | Строка | Что меняет |
|---|---|---|
| `New()` | 39 | Копирует `hidden_nodes`, force-исследует 11 стартовых узлов, платит bounty |
| `get_problem_computer_charges()` / `consume_problem_computer_charge()` | 48 / 61 | Ленивое пополнение и списание `problem_computer_charges` |
| `recalculate_nodes()` | 164 | Перестраивает `researched_designs`, может стереть `custom_designs` |
| `add_point_list(list, income=TRUE)` | 183 | `next_income[]` или сразу `research_points[]`; только положительные известные типы |
| `add_points_all` | 193 | Через `add_point_list` в `next_income` |
| `commit_income()` | 199 | Переносит `next_income` в `research_points` |
| `remove_point_list` / `remove_points_all` | 205 / 210 | `research_points[] = max(0, …)`; второй без вызовов |
| `modify_point_list` / `modify_points_all` | 216 / 221 | `research_points[] ± x`, ноль пропускается |
| `copy_research_to(receiver)` | 227 | У получателя: `hidden_nodes -=`, `research_node_id(force)` с bounty, designs, `recalculate_nodes` |
| `copy()` | 248 | Прямое копирование списков; вызовов нет |
| `add_point_type` | 266 | `next_income[]` или `research_points[]` |
| `discover_gas_synthesis` | 279 | `synthesized_gases[]` и немедленные очки |
| `/proc/register_gas_synthesis(_from_mixture)` | 306 / 313 | Обёртки, всегда `science_tech` |
| `add_design(_by_id)` / `remove_design(_by_id)` | 335–349 | `researched_designs[]`, `custom_designs[]`; фильтры `specialized` (507) и `exofab/training` (`antag_training_sector_robotics.dm:125`) |
| `research_node(_id)` | 374 / 371 | Списывает цену (382), открывает узел, designs, bounty (392–395) |
| `boost_with_path` | 407 | `boosted_nodes`, раскрытие скрытых узлов |
| `update_tiers` / `update_node_status` | 418 / 436 | Производные списки доступности |
| `autounlocking/New` → `autounlock()` | 518 / 522 | Локальные designs машин |
| `techweb_node/get_price(host)` | `_techweb_node.dm:86–94` | **Ошибка:** уменьшает общий `research_costs` узла при каждом вызове с boost |

`add_point_list` и `add_point_type` по умолчанию пишут в `next_income`. В баланс доход попадает только из `SSresearch.fire()` → `commit_income()`, и только у web с работающим сервером. Немедленно очки пишут `astro_sensor`, `discover_gas_synthesis` и discoverable component.

## Начисление очков — 41 место

| Файл:строка | Proc | Web | D/I | Изменение |
|---|---|---|---|---|
| `code/controllers/subsystem/research.dm:377,378` | `SSresearch/fire` | `stored_research` работающих серверов | I | Пассивный доход и `commit_income` |
| `code/controllers/subsystem/research.dm:382,386` | `fire` | то же | D | `last_bitcoins`, только отображение |
| `code/datums/components/nanites.dm:505` | `nanites/add_research` | `science_tech` | I | Неопределённый тип очков отфильтровывается; фактически no-op |
| `code/game/machinery/doppler_array.dm:223,226,231` | `doppler_array/research/sense_explosion` | `linked_techweb` | D, I | `largest_values` и очки за взрыв |
| `code/modules/antagonists/nukeop/equipment/nuclear_challenge.dm:108` | `war_was_declared` | `science_tech` | I | +5000 |
| `code/modules/atmospherics/gasmixtures/reactions.dm:71` | `report_synthesis` | `science_tech` | I | Первое получение газа; вызовы из `native_atmos_bindings.dm:1637`, `reactions.dm:310,930` |
| `reactions.dm:691` | `bzformation/react` | `science_tech` | I | **Ошибка:** лимит передан как `income`, награда фактически без cap |
| `reactions.dm:738,796,843` | `stimformation`, `nobliumformation`, `miaster` | `science_tech` | I | Очки реакции |
| `reactions.dm:933,937,938,940` | `hagedorn/react` | `science_tech` | D, I | `largest_values[GAS_QCD]` и очки |
| `electrolyzer_reactions.dm:39` | `report_synthesis` | `science_tech` | I | Первое получение газа; вызовы 81, 119, 164 |
| `hfr_main_processes.dm:201` | `fusion_process` | `science_tech` | I | Газы из смеси |
| `code/modules/mob/living/carbon/life.dm:712,794` | `handle_status_effects` | `science_tech` | I | Ballmer peak |
| `code/modules/modular_computers/file_system/programs/tetris.dm:149` | `tetris/ui_act` | `science_tech` | I | Аркада |
| `code/modules/power/singularity/collector.dm:80` | `rad_collector/process` | `science_tech` | I | Режим tritium на станции |
| `code/modules/power/tesla/coil.dm:104,146` | `zap_act` | `linked_techweb` | I | Очки катушек |
| `code/modules/research/destructive_analyzer.dm:140,141` | `user_try_decon_id` | консоль | I, D | Очки предмета и `deconstructed_items`; veil crystal исключён из dedupe |
| `code/modules/research/experimentor.dm:468` | `experiment` | `find_rnd_network_for_object` | I | +5000 за relic |
| `code/modules/research/nanites/nanite_programs/utility.dm:170,205` | `active_effect` | null | I | Мёртвый путь |
| `code/modules/surgery/experimental_dissection.dm:91,111` | `success` / `failure` | `linked_techweb` операционной | I | Очки вскрытия |
| `modular_bluemoon/code/game/machinery/astro_sensor.dm:206` | `collect_data` | `science_tech` | I | Немедленные очки |
| `modular_bluemoon/code/modules/events/minor_incidents/bot_firmware_glitch.dm:388` | `upload` | консоль или `science_tech` | I | +1000 |
| `modular_bluemoon/white_steel/inner/super_cruise/orbital_poi_generator/loot/alien_artifact.dm:62,481` | `attackby`, `insanity_pulse/pulse` | ближайшая сеть, `science_tech` | I | +7500 и 5k–20k |
| `modular_bluemoon/white_steel/inner/discovery_research/discoverable_component.dm:45` | `discovery_scan` | web сканера | **D** | `research_points +=` в обход примитивов и дохода |
| `modular_sand/code/game/machinery/computer/arcade/tetris.dm:204` | `tetris/ui_act` | `science_tech` | I | Аркада |
| `modular_sand/code/game/objects/items/computermath.dm:514` | `handle_reward` | `linked_techweb` | I | Problem Computer |
| `modular_splurt/code/game/machinery/research_table.dm:166` | `on_cum` | `linked_techweb` | I | Очки стола |
| `modular_splurt/code/modules/research/xenoarch/artifact.dm:37,56,75` | `attackby` | ближайшая сеть | I | Археология +4500/+9000 |

## Списание очков — 5 мест

| Файл:строка | Proc | D/I | Изменение |
|---|---|---|---|
| `code/modules/research/rdconsole.dm:193` | `rdconsole/research_node` из `ui_act:469` | I | Единственная игровая покупка: цена, unlock, bounty, сигнал синхронизации станков |
| `code/modules/mob/living/carbon/life.dm:715,797` | `handle_status_effects` | I | Штраф Ballmer |
| `code/modules/mod/modules/module_hacker.dm:329` | `hack_research` | I | `modify_points_all(0)` должен обнулять, но ничего не делает |
| `modular_sand/code/game/objects/items/computermath.dm:497` | `handle_reward` | I | Штраф за неверный ответ |

## Открытие знаний — 9 мест

`destructive_analyzer.dm:122` и `experimentor.dm:205` (`boost_with_path`); `rdconsole.dm:211–213` (`research_logs`, D); `dna_console.dm:1051,1113,2107` (`discovered_mutations`, D); локальные web машин: `autolathe.dm:437,439`, `limbgrower.dm:458`, `biogenerator.dm:57,59`. Газовые открытия учтены в начислении.

## Копирование и перепривязка знаний — 23 места

| Группа | Места |
|---|---|
| Диски в консоли | `rdconsole.dm:495,529` (`add_design`), `:535` загрузка tech disk через `copy_research_to` с bounty, `:541` запись на диск с bounty у диска, `:518–519` замена web диска, `:483–488` запись design disk **с изменением глобального `/datum/design`** |
| Станки и фабрикаторы | `_production.dm:133` (`update_research` из 44, 77, 121, 318, 512), `:71–72` перепривязка, `mech_fabricator.dm:535` |
| Ниндзя и цель download | `objective.dm:913,916`, `ninjaDrainAct.dm:123` и дублирующий `:139`, мёртвый `suit_attackby.dm:37` |
| Design disk в машинах | `autolathe.dm:268`, `biogenerator.dm:175`, `machine_redemption.dm:392` |
| Loot-диск | `research_disks.dm:11` пишет `researched_nodes` напрямую |
| Перепривязка ссылок | `techweb_holder.dm:19,30,35`, `server.dm:161` через `vars[]`, `discovery_scanner.dm:31`, обработчики `on_techweb_changed` в rdconsole, server, dna_console, coil, doppler, computermath, research_table, Operating, nanite hub, component_printer |

## Экономика, пулы и bootstrap

- **Bounty:** `_techweb.dm:392–395` в каждом `research_node`, кроме `admin` и `exofab/training`. Поэтому bounty платят создание web, загрузка дисков, синхронизация станков, ninja drain и проверка objective. Сопряжённые выплаты: `doppler_array.dm:228–230` (SCI), `coil.dm:100–102,142–144` и `collector.dm:77–79` (ENG). На старте раунда SCI-счёт ещё не создан.
- **Пулы:** `computermath.dm:595,602,609,614` (`problem_computer_charges` на web); cargo-пул `shuttle.dm:146–148` и `computermath.dm:566–567`; `techweb/bepis/New` (`_techweb.dm:144–153`) уменьшает глобальный `techweb_nodes_experimental`; `raw_anomaly.dm:71` (`created_anomaly_types`).
- **Bootstrap и admin:** `research.dm:324,325` (`science_tech`, `admin_tech`), `get_rnd_network(:410)` и `get_rnd_network_for(:421,423)`, `admin/New` с `research_points = INFINITY`, фракционные и away-mission web с принудительными узлами, `nanite_program_hub/admin`. Отдельных admin verbs для research нет; доступ через VV, ProcCall и SDQL.

## Создание techweb

Глобальные `science_tech` и `admin_tech`; реестр `SSresearch.rnd_networks` для syndicate, tarkon и inteq; незарегистрированные изолированные web для удалённых серверов и станков; локальные web lathe/imprinter, exofab, autolathe, makeshift autolathe, limbgrower, biogenerator, ore machines и учебного фабрикатора; web дисков, ниндзя, objective и away missions. `SSresearch.techwebs` никогда не заполняется.

## Тесты, которые нужно учесть при миграции

`techweb_copy.dm`, `techweb_machine_initialization.dm`, `atmos_gas_balance.dm`, `astro_sensor.dm`, `minor_incidents_engineering.dm`, `minor_incidents_outside.dm`, `heretic_veil_crystal.dm`, `antag_training_sector_robotics.dm`, `bugreport_fixes_2026_10.dm`, `round_10463_10470_runtimes.dm`, `round_9752_regressions.dm`, `station_map_equipment.dm`, `brig_assistant_console.dm`, `atmos_engineering_fixes.dm`, `supporting_tail.dm`, `harddel_round_9827.dm`, `machinery_optimization.dm`.

## Итог и решения для P5

| Категория | Мест | Примечание |
|---|---:|---|
| Начисление | 41 | 5 прямых, 3 мёртвых |
| Списание | 5 | 1 no-op |
| Открытие знаний | 9 | 4 прямых |
| Копирование и перепривязка | 23 | 1 мёртвое, 1 дубль |
| Экономика | 5 | Примитив bounty и 4 выплаты |
| Пулы | 8 | 2 cargo-пула вне techweb |
| Bootstrap и admin | 13 | Плюс 8 тестовых мест |

Решения до authoritative research:

1. `get_price` портит общую цену узлов. Ledger должен владеть ценой и не повторять эту ошибку. Исправление legacy-поведения меняет баланс и требует отдельного решения.
2. `writeDesign` меняет общий `/datum/design`. Нужен read-only catalog.
3. Очки web без работающего сервера остаются в `next_income`. Миграция переносит только подтверждённый баланс и явно классифицирует `next_income`.
4. Синхронизация станков платит bounty за каждый новый узел на каждой машине. Новые правила §13.5 отменяют bounty у `Replicated`, `Restored` и `Imported`.
5. Мёртвые writers (nanites, module_hacker, nanite programs, fallback вскрытия, ninja disk) способны ожить после исправлений. Search gate проверяет их наравне с живыми.
6. Награда BZ без лимита и прямые записи discoverable component обходят общие правила. В core они становятся `ResearchEvidence` с проверкой однократности.

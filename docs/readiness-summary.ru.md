# Готовность интеграции netDM-core × BlueMoon (сводка, 17.11.2026)

Документ отвечает на три вопроса: что реализовано, чем это доказано и что осталось доказать. Подробности по этапам — `implementation-status.ru.md`; ручные сценарии — `manual-bryond-check.ru.md`.

## 1. Пять пунктов цели

| # | Пункт | Состояние | Где доказательство |
|---|---|---|---|
| 1 | Safety gates research: единый writer, полнота зеркала, защита подтверждённых записей журнала, подтверждение на DreamDaemon | Реализовано | DreamDaemon `research_*` (authoritative PASS, `mirror=complete rejected=0 dropped=0 unmirrored=0`), core smoke (повреждение середины журнала, отказ записи, восстановление), `host-ipc` Windows/Linux, Linux Compose replay журнала |
| 2 | Полноценное погружение через нейролинк и точку доступа | Реализовано | DreamDaemon `network_immersion`, `network_immersion_dive`: вход, транс (рук/оверлей), личность, потеря точки доступа, выход по `presence_lost`, повторный вход; `host-ipc` (присутствие, вторжение, разрыв канала) |
| 3 | Сетевые сущности, живущие в сети | Реализовано | программы/присутствия/личности игроков/NPC-агенты с целями и задачами/враждебные агенты; core smoke контрактов, `host-ipc` (лица, население, вторженцы), DreamDaemon-погружения |
| 4 | Иммерсивная визуализация сети и активности в TGUI/PixiJS | Реализовано | `tsc` + `jest NetdmTopology` 18/18 + сборка бандла; реальный Chromium WebGL и canvas-fallback (`scripts\test-netdm-scene-browser.ps1`, `NETDM_SCENE_BROWSER_PASS`); скриншоты `TestResults\p13\browser-proof\` |
| 5 | Слой внедрён всюду, где полезно, и по умолчанию не навязан | Реализовано | флаги `NETDM_OBSERVE`, `*_MODE`, `NETDM_NET_LIFE`, `NETDM_NET_IMMERSION` (в Compose/бандле по умолчанию `0`); интеграции: терминал/КПК/имплант, R&D, двери, камеры, атмосфера, радио, КПК-мессенджер, панель АПЦ, админ-инструменты, событийный сид, эмаг-вторженец; все проверки — DreamDaemon/`host-ipc` |

## 2. Платформы и поставка

| Гейт | Результат | Артефакт |
|---|---|---|
| Linux Compose (production) | **BLUEMOON_COMPOSE_PASS** на текущем дереве с P13-флагами; SIGKILL Host → пересоздание → replay журнала; раунд в MariaDB | `TestResults\p1\release-manifest.p13.linux.json`; в логе мира `netDM network life: seeded 3 agents` |
| Windows native bundle | **BLUEMOON_WINDOWS_PASS** с P13 в автотесте; BYOND 516.1688, MariaDB 11.4.13, Node 24.16.0, host runtime 10.0.15 | `TestResults\p1\release-manifest.windows.p13.json`, `TestResults\p13\windows-bundle-acceptance-evidence.txt` |
| Пиксельная сцена в браузере | **NETDM_SCENE_BROWSER_PASS** (WebGL + canvas) | `TestResults\p13\browser-proof\` |
| P13 Host на Linux | `NETDM_IPC_PASS` (UDS, контейнер `dotnet/sdk:10.0`) | вывод `tests/host-ipc.ps1` |

## 3. Как воспроизвести

```powershell
pwsh -File scripts\check-all.ps1 -Only host      # сборка ядра, smoke, контракты
pwsh -File scripts\check-all.ps1 -Only browser   # сцена в реальном Chromium
pwsh -File scripts\build-bluemoon-unit-dmb.ps1 -GameRepository ..\MOLOT-BlueMoon-Station -BuildDirectory TestResults\p1\game-src
pwsh -File scripts\test-bluemoon-unit.ps1 -BuildDirectory TestResults\p1\game-src -BridgeDll TestResults\p3-bridge\netdm_byond.dll -TestList scripts\bluemoon-netdm-tests.txt
pwsh -NoProfile -File tests\host-ipc.ps1
pwsh -File deploy\bluemoon\Test-Compose.ps1 -EnvironmentFile <env> -ProjectName netdm-accept -ManifestPath <manifest>
pwsh -File deploy\bluemoon\Test-WindowsBundle.ps1 -Bundle <распакованный бандл> -NetworkLife
```

## 4. Что осталось доказать (требует игровой среды)

- Реальный клиент BYOND (встроенный CEF): окно сети, PixiJS-рендерер, пульсация, маркеры агентов, звук тревоги и плашки, погружение «глазами игрока».
- Плейтест 2–3 игроков: ощущается ли сеть живой; время переходов/свежесть знания; читаемость угроз; цена погружения; влияние на раунд.
- Подстройка чисел по итогам плейтеста: удержание вторженца 10 с, пульсация 6 с, лимиты агентов (16, спавн ≤8, активность 256, страница 64), время транса, цена топлива.
- Обновление/rollback под нагрузкой и повторный полный Compose после ручной приёмки.
- ~~Аудит оставшихся 7 карт ротации~~ — закрыто: все 24 карты ротации прогнаны (раунд 19), невыгруженных частей нет; отчёт в `TestResults\map-audit-p13\summary.md`. Осталось: живая нагрузка и оценка баланса.

## 5. Известные ограничения (не скрывать)

- ~~В трансе клики мира не блокируются~~ — исправлено: пока оператор в погружении, клики в мир перехватываются и он получает подсказку вернуться из сети (тест `network_immersion_dive`).
- NPC-агенты по-прежнему не вызывают сервисы устройств (команды/эффекты), но уже оставляют физические следы: сигналы у занятых АПЦ и выбитый рубильник при вторжении (раунд 20, флаг `NETDM_NET_LIFE`).
- Окно сети и раздел АПЦ не проверены в клиенте BYOND вручную (только Chromium + юнит-тесты TGUI).
- Сид населения станцией и эмаг-вторженец подтверждены через control plane (`host-ipc`, Compose, Windows-бандл); в живом раунде с игроками не наблюдались.
- Числа баланса не откалиброваны плейтестом.
- Другие операторы видны в сети (кольца цвета личности на карте), у личности есть статус; население меняется само (раунд 21) — проверить в клиенте BYOND и плейтесте.

## Реализация после первичной сводки (раунды 21–36)

Добавлено поверх базовых пяти пунктов (всё за флагами `NETDM_NET_LIFE`/`NETDM_OBSERVE`, тяжёлые проверки отложены по указанию владельца):

- **Многопользовательская сеть:** другие операторы видны на карте (кольца цвета личности), в списке «Операторы в сети» и в `presences`; у личности есть статус и автоматическая должность со станции.
- **Живое население:** агенты появляются/уходят сами (раз в 6–14 минут), «бормочут» в общий канал; цели расширены: диверсант выбивает рубильник АПЦ, инженер восстанавливает питание (питание зоны реально переключается), плюс курьер, страж, наблюдатель, архивист.
- **Контр-игра игрока:** из окна сети можно выгнать вторженца с узла, вызвать курьера/инженера/стража/наблюдателя/архивиста, пользоваться общим каналом чата (узел ↔ канал).
- **Журнал происшествий:** раундовый (до 50 строк) в окне плюс записи в `game.log`; в панели АПЦ видно число агентов у узла.
- **Лента активности:** вход/выход операторов видны другим; админ-глагол «Network Incidents» выводит последние 10 происшествий в чат.
- **Интеграции:** окно сети из АПЦ, консолей камер и R&D; админ-глаголы открытия окна и управления населением (посмотреть/добавить/убрать).
- **Погружение:** трансом блокируются клики в мир, есть часы в сети, тихие напоминания, легенда карты; подписи агентов и операторов с должностью на карте.

Проверки этих итераций: только компиляционные (DreamMaker 0/0, Host build 0 ошибок, `tsc` без ошибок). Общий прогон DreamDaemon/Compose/бандла/`host-ipc`, ручной клиент BYOND и плейтест — отдельным пакетом позже.

## План проверок после итераций 21–38 (когда будете готовы)

Все накопленные изменения компилируются (Host 0 ошибок, DreamMaker 0/0, `tsc` без ошибок), но тяжёлые прогоны после этих итераций ещё не выполнялись. Рекомендуемый порядок — от самого дешёвого к самому дорогому:

1. `dotnet run --project tests\NetDM.SmokeTests -c Release` — контракты ядра и схемы (включая новые поля `presences`, `personaStatus`, `personaRole`, `channel`, `inspect`/`banish`/`call_agent`).
2. `pwsh -NoProfile -File tests\host-ipc.ps1` — Windows pipe: население, лица, вторженцы, журнал повреждения, новые опкоды.
3. `pwsh -File scripts\build-bluemoon-unit-dmb.ps1 ...` + `pwsh -File scripts\test-bluemoon-unit.ps1 ... -TestList scripts\bluemoon-netdm-tests.txt` — полный DreamDaemon-набор (включая `network_life_physical`, погружения, исследования).
4. TGUI: `node .yarn/releases/yarn-*.cjs tsc --noEmit`, `jest packages/tgui/interfaces/NetdmTopology`, `tgui:build`; плюс `pwsh -File scripts\test-netdm-scene-browser.ps1`.
5. Linux Compose: синхронизировать `E:\netdm-compose\source` (изменились game-файлы раундов 21–38), `docker compose ... build game`, затем `Test-Compose.ps1 -EnvironmentFile ... -ProjectName netdm-final -ManifestPath ...` с `NETDM_NET_LIFE=1`; проверить `seeded 3 agents` и PASS.
6. Windows bundle: пересборка `Build-WindowsBundle.ps1` из текущего мира и `Test-WindowsBundle.ps1 -NetworkLife`; ожидается PASS и строка сида.
7. Ручной клиент BYOND и плейтест по [чек-листу](manual-bryond-check.ru.md): погружение (часы, клики заблокированы), карта (подписи/кольца/легенда/журнал происшествий), общий канал, изгнание вторженца, вызов инженера, консоли камер/R&D, панель АПЦ (число агентов).

Ожидаемые артефакты: `TestResults\p1\release-manifest.p13.linux.json`, `release-manifest.windows.p13.json`, `TestResults\p13\*` (свидетельства), логи прогонов.

## Взломы, доступы и анонимность (P13.5)

- **Взлом:** выбор узла → код от Host на 15 с → ввод в окне; успех даёт доступ к узлу на 10 мин, провал поднимает тревогу и перезарядку.
- **Доступы:** кольцо украденных доступов в окне; использование авторизует Host, мир выполняет эффект (сейчас — переключение питания АПЦ).
- **Анонимность:** маскировка личности для других операторов (лента/канал — «скрытый оператор»), кулдаун 30 с; физическое местоположение не скрывается.
- Проверки слоя: core smoke (схемы), компиляция DM/TGUI; DreamDaemon и живая проверка — в общем пакете.

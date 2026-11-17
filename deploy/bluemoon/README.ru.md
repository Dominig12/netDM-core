# Поставка BlueMoon с netDM Observe

Комплекты запускают обычный BlueMoon world с MariaDB 11.4.13 и IPC 1.1. Observe включён. Внутреннее окно Observatory показывает опубликованный граф APC. Игровые сервисы BlueMoon остаются владельцами своих состояний.

## Linux Compose

Создайте отдельный env-файл на основе `.env.example`. Укажите абсолютные пути к двум отдельным файлам с паролями из букв, цифр, `_` и `-`. Пароли нужны только при запуске. Для сборки укажите `BLUEMOON_SOURCE` на экспорт ветки BlueMoon с Observe. `Export-Source.ps1` собирает экспорт из Git и перечисленных файлов Observe. Конфигурация берётся из `HEAD`.

```powershell
pwsh -File deploy/bluemoon/Export-Source.ps1 -GameRepository ../MOLOT-BlueMoon-Station -Destination /path/to/source
docker compose -f deploy/bluemoon/compose.yaml --env-file /path/to/bluemoon.env build
docker compose -f deploy/bluemoon/compose.yaml --env-file /path/to/bluemoon.env up -d --no-build --wait --wait-timeout 900
```

Для запуска готовых образов загрузите их в локальный Docker и используйте последний вызов с именами из release manifest. На сервере нужны Docker Engine и Compose. Стек публикует только игровой порт, по умолчанию на loopback. Диагностика доступна локально в контейнере Host. Внутренний порт readiness отдаёт только проверки IPC, network и game. Game readiness требует готового тома `research`: стек с журналом исследований, в который нельзя писать, здоровым не считается.

MariaDB создаёт базовую схему, BlueMoon, whitelist и mentor таблицы только в пустом volume. `db-init` проверяет ревизию 4.7 до запуска мира. Постоянные volumes `database`, `game-config` и `game-data` сохраняются при `docker compose stop`. На каждом старте launcher генерирует `game-config/entries/dbconfig.txt` из секрета. Host при отказе packet worker завершается с кодом 70, и Compose перезапускает его по `restart: on-failure`. Для независимой проверки запускайте `Test-Compose.ps1` с новым именем проекта и отдельными секретами. С параметром `-ManifestPath` после успешной приёмки он записывает release manifest с ID проверенных образов, закреплёнными базовыми образами, версией DreamDaemon, источником игры и признаком незакоммиченного дерева ядра.

Приёмка Compose требует, чтобы пересозданный Host воспроизвёл журнал того же раунда из тома `research`. Для штатной остановки она проверяет код завершения контейнера DreamDaemon и отметку о закрытии текущего раунда в MariaDB.

## Windows x64

Распакуйте комплект в отдельный закрытый для других пользователей каталог и запустите `Start-BlueMoon.ps1` в Windows PowerShell. Для другого экземпляра задайте уникальные `-GamePort`, `-HostPort` и `-DatabasePort`. Лаунчер запускает portable MariaDB, self-contained Host и вложенный DreamDaemon. Путь к Host и PID процессов появляются в `state/run.json`, когда готова сетевая сессия мира. Журнал исследований раунда лежит в `state/research`. Если в него нельзя писать, лаунчер предупреждает и продолжает раунд: исследования закрыты до восстановления, остальная сеть работает.

Для остановки создайте `state/stop.request` либо остановите launcher с клавиатуры. Локальный marker завершает игровой мир и закрывает MariaDB. При падении Host launcher запускает новый Host на том же pipe и ждёт публикации нового snapshot. Больше пяти падений за десять минут останавливают экземпляр с ошибкой. База остаётся в `state/database`, игровые файлы в `game/data`. `state/db-*.ini` и `game/config/entries/dbconfig.txt` содержат локальные учётные данные. Храните каталог экземпляра под контролем оператора.

При остановке через `state/stop.request` лаунчер возвращает ошибку, если DreamDaemon не завершился в отведённое время или вернул ненулевой код. Приёмка комплекта также проверяет, что после перезапуска Host открыт тот же журнал из `state/research`.

Чтобы собрать архив заново, выполните штатный `tools/build/build.bat build --warnings-as-errors` в отдельном checkout игры, соберите Win32 bridge (без Visual Studio: `scripts/build-bridge-mingw.ps1`) и выполните `dotnet publish src/NetDM.Host -c Release -r win-x64 --self-contained`. Передайте собранные каталоги, чистый экспорт исходников и `bin` BYOND 516.1688 в `Build-WindowsBundle.ps1`. MariaDB ZIP автоматически скачивается с архива вендора и проверяется по SHA-256. Подайте разрешённые VC++ redistributable DLL нужной архитектуры через `-VcRuntimeBin` и `-VcRuntime64Bin`. `Test-WindowsBundle.ps1` проверяет свежую распаковку с системным PATH, собственными портами, пустой базой, recovery и остановкой мира.

Версии BYOND и Node соответствуют `dependencies.sh` игры. Linux BYOND поставляется из закреплённого OCI-образа. Windows сборка использует файлы BYOND из дистрибутива 516.1688. [Релиз rust-g 6.2.0](https://github.com/tgstation/rust-g/releases/tag/6.2.0) · [Portable MariaDB для Windows](https://mariadb.com/docs/server/server-management/install-and-upgrade-mariadb/installing-mariadb/binary-packages/installing-mariadb-windows-zip-packages).

## Стенд для ручной проверки на Windows

Нужны Git, .NET 10 SDK, Node.js 24, Visual Studio с C++ (CMake входит в набор «Разработка классических приложений на C++») и BYOND 516.1688. Оба репозитория кладутся рядом по короткому пути, иначе DreamMaker упирается в длину пути:

```powershell
mkdir C:\netdm; cd C:\netdm
git clone -b feature/byond-native-bridge https://github.com/Dominig12/netDM-core.git
git clone -b net-feature-dmcore-new https://github.com/Dominig12/MOLOT-BlueMoon-Station.git
cd netDM-core
pwsh -File scripts\setup-bluemoon-stand.ps1
pwsh -File scripts\play-bluemoon.ps1 -Map metastation -NetworkCameras
```

`setup-bluemoon-stand.ps1` проверяет инструменты, собирает Win32 bridge через Visual Studio, экспортирует исходники BlueMoon в `TestResults\p1\game-src` и ставит пакеты TGUI. Он нужен один раз на checkout. `play-bluemoon.ps1` собирает Host, TGUI и мир, запускает их и печатает адрес для DreamSeeker и список того, что проверить. Режимы: `-Mode shadow|authoritative` (исследования, мессенджер, радио), `-NetworkCameras` (охранные консоли через сеть), `-Map metastation|boxstation|runtime`, `-NoBuild` для повторного запуска без сборки. Остановка — Ctrl+C или файл `TestResults\p1\game-src\netdm-stand.stop`.

Все автоматические проверки одной командой: `pwsh -File scripts\check-all.ps1 -Only all`. Состояние запущенного стенда: `pwsh -File scripts\stand-status.ps1`. Полный справочник команд, тестов и ручных сценариев в игре: [docs/testing-guide.ru.md](../../docs/testing-guide.ru.md).


## P13: сеть как игровой слой (17.11.2026)

Поставка не меняется: P13-функции включаются флагами и не влияют на обычный раунд. Compose прокидывает их с дефолтом `0`:

- `NETDM_NET_LIFE=1` (env-файл) → `NETDM_NET_LIFE` у мира и `NetDM__NetLife` у Host — население сети (NPC-агенты, сид станцией, эмаг-вторженец).
- `NETDM_NET_IMMERSION=1` → погружение через нейролинк.
- Окно сети и панель АПЦ требуют `NETDM_OBSERVE=1`; раздел «Network» в панели АПЦ скрыт без Observe.

Полный Compose и Windows bundle по-прежнему разворачивают Observe; для приёмки P13 добавьте флаги в env-файл и пройдите [чек-лист клиента BYOND и плейтеста](../../docs/manual-bryond-check.ru.md).

Windows bundle: лаунчер принимает `-NetworkLife` (включает `NETDM_NET_LIFE=1` и `NETDM_NET_IMMERSION=1` для мира и `NetDM__NetLife=1` для Host); `Test-WindowsBundle.ps1 -NetworkLife` прокидывает переключатель в приёмку.

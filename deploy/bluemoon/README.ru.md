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

## Windows x64

Распакуйте комплект в отдельный закрытый для других пользователей каталог и запустите `Start-BlueMoon.ps1` в Windows PowerShell. Для другого экземпляра задайте уникальные `-GamePort`, `-HostPort` и `-DatabasePort`. Лаунчер запускает portable MariaDB, self-contained Host и вложенный DreamDaemon. Путь к Host и PID процессов появляются в `state/run.json`, когда готова сетевая сессия мира. Журнал исследований раунда лежит в `state/research`. Если в него нельзя писать, лаунчер предупреждает и продолжает раунд: исследования закрыты до восстановления, остальная сеть работает.

Для остановки создайте `state/stop.request` либо остановите launcher с клавиатуры. Локальный marker завершает игровой мир и закрывает MariaDB. При падении Host launcher запускает новый Host на том же pipe и ждёт публикации нового snapshot. Больше пяти падений за десять минут останавливают экземпляр с ошибкой. База остаётся в `state/database`, игровые файлы в `game/data`. `state/db-*.ini` и `game/config/entries/dbconfig.txt` содержат локальные учётные данные. Храните каталог экземпляра под контролем оператора.

Чтобы собрать архив заново, выполните штатный `tools/build/build.bat build --warnings-as-errors` в отдельном checkout игры, соберите Win32 bridge (без Visual Studio: `scripts/build-bridge-mingw.ps1`) и выполните `dotnet publish src/NetDM.Host -c Release -r win-x64 --self-contained`. Передайте собранные каталоги, чистый экспорт исходников и `bin` BYOND 516.1688 в `Build-WindowsBundle.ps1`. MariaDB ZIP автоматически скачивается с архива вендора и проверяется по SHA-256. Подайте разрешённые VC++ redistributable DLL нужной архитектуры через `-VcRuntimeBin` и `-VcRuntime64Bin`. `Test-WindowsBundle.ps1` проверяет свежую распаковку с системным PATH, собственными портами, пустой базой, recovery и остановкой мира.

Версии BYOND и Node соответствуют `dependencies.sh` игры. Linux BYOND поставляется из закреплённого OCI-образа. Windows сборка использует файлы BYOND из дистрибутива 516.1688. [Релиз rust-g 6.2.0](https://github.com/tgstation/rust-g/releases/tag/6.2.0) · [Portable MariaDB для Windows](https://mariadb.com/docs/server/server-management/install-and-upgrade-mariadb/installing-mariadb/binary-packages/installing-mariadb-windows-zip-packages).

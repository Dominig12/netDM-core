# NETRUN — документация

Целевая спецификация — `NETRUN_Implementation_Specification_RU.md` (у владельца проекта, вне репозитория).
Текущее состояние кода — [status.md](status.md). Этот файл только связывает документы.

| Документ | О чём |
|---|---|
| [t0-audit.md](t0-audit.md) | Аудит обоих репозиториев: карта решения, модель потоков, очереди, IPC, точки расширения, инвентарь DM-устройств, baseline, соответствие MUST |
| [migration-matrix.md](migration-matrix.md) | Решение по каждому механизму: reuse / adapt / replace / remove / defer, с обоснованием и этапом |
| [architecture.md](architecture.md) | Целевые слои, направление зависимостей, владение состоянием, конвейер действия, ресурсы, права, каналы проверки |
| [adr/README.md](adr/README.md) | ADR-0001…0006: размещение, один писатель, реестр действий, сборка без `.sln`, ограничения стенда, виртуальные ресурсы; ADR-0007 — снятие легаси, ADR-0008 — бенчмарки вне production-хоста |
| [roadmap.md](roadmap.md) | Этапы N0…N9 с зависимостями и gates |
| [status.md](status.md) | Что сделано, что реально проверено, технический долг, что осталось |
| [legacy-removal.md](legacy-removal.md) | Очередь снятия легаси: что удаляется, почему и чем проверено (ADR-0007) |

## Быстрые команды

```powershell
dotnet build src/NetDM.Host -c Release                 # сборка всего C#-слоя, 0 предупреждений
dotnet run --project tests/NetRun.Tests -c Release     # домен NETRUN, 479 утверждений
dotnet run --project tests/NetDM.SmokeTests -c Release # транспорт и сервисы
pwsh -NoProfile -File tests/host-ipc.ps1               # живой Host через IPC: NETDM_IPC_PASS
pwsh -File scripts/check-all.ps1 -Only host            # группа проверок Host целиком
```

## Границы, которые нельзя размывать

1. `NetDM.Core` не знает о NETRUN и о DM.
2. `NetDM.Services` — транспортные контракты; игровой контент туда не добавляется (существующий переносится, см. матрицу T-09…T-12).
3. `NetRun.*` зависит от `NetDM.*`, не наоборот.
4. Только `NetDM.Host` собирает оба слоя (ADR-0001).
5. Ни одно игровое сетевое взаимодействие не идёт в обход NETDM (G-001, INT-001).
## Состояние на конец сессии 26.11.2026

| Проверка | Результат |
|---|---|
| Домен (`tests/NetRun.Tests`) | 479 утверждений, PASS |
| Хост (`scripts/check-all.ps1 -Only host`) | PASS (сборка, smoke, домен, живой IPC) |
| Окно (`jest NetrunView`) | 29 тестов, PASS |
| Живой мир (`scripts/bluemoon-netdm-tests.txt`) | **23 из 24**; единственное падение `door_station` — в герметичном мире нет кнопок blast-дверей |
| Сквозной путь NETRUN в игре | ядро → присутствие → переход по маршруту (`netrun_client_life`) |

Работа не завершена: остался легаси-слой L-8 (бенчмарки в production-хосте), слои карты UI-003 и остаток контента §12 (Lockdown, вирусы по маршрутам, след в знаниях). Точки входа и препятствия — в [status.md](status.md) §23.
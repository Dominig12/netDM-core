# Расширение Host: модули команд и сетевых контрактов

## Два различных расширения

1. **Сетевой сервис**, то есть взаимодействие двух узлов: реализуйте `IServiceContractModule` в `NetDM.Services`. Зарегистрируйте реализацию в DI как `IServiceContractModule`. `GameNetworkService` собирает реестр контрактов до создания первой сессии и передаёт тот же реестр всем новым сессиям после reconnect/snapshot. Удалённые вызовы **обязательно** должны проходить через `ServiceRuntime.Invoke/Publish` и доставку пакетами.
2. **Локальная команда control plane Host**, например research catalog/journal, диагностика или управление жизненным циклом: реализуйте `IHostIpcModule` в Host. Перечислите операции в `Operations`, укажите `RequiresNetworkReady`, зарегистрируйте в `Program.cs` через `AddSingleton<IHostIpcModule, MyIpcModule>()`. Список операций и обработчики больше не редактируются в большом `PipeHostedService` switch. Конфликт имён останавливает запуск.

## Строгая граница

Ни один `IHostIpcModule` не должен предоставлять обход доставки DM → DM через прямой доступ к мировым объектам. В отличие от команд нового сервиса, локальные IPC-команды управляют только состоянием самого Host (топология, регистрируемые доменные данные, bootstrap). Их вызовы ограничены согласованием v1, owner fence, stream id и порядковым номером команды на уровне `PipeHostedService`.

Готовность сети проверяется перед исполнением модулей, требующих `RequiresNetworkReady`. Изменение или расширение протокола требует собственных контрактов, лимитов payload, версионирования и тестов на недостаточную готовность и повторное исполнение. `PipeHostedService` сохраняет владение сессией и IPC-транспортом.

## Host-provided network services

When a command has physically reached a Host-provided service endpoint, it must be answered by a registered `IHostServiceExtension`, not by a large switch in `ResearchHost`. Implement `ServiceType` and `Answer(GameNetworkService, HostCommand, out Action?)` in a stateless handler, then add one DI registration. `GameNetworkService` derives the Host service type set from the same registrations; an unknown or duplicate name fails closed. The built-in `research.server` is kept local to `ResearchHost`, which owns its durable ledger. The optional post-ack callback is invoked only when the service successfully acknowledges an arrived command. Do not execute DM gameplay effects from these handlers.


## Расширение сетевых NPC через стратегии

`INetworkBotBehavior` из `NetDM.Services` определяет один вид сетевых сущностей. Чтобы добавить NPC, не нужно добавлять вариант в enum и менять `NetworkSpace.LifePass` или `network_life` IPC-dispatcher.

Минимальная регистрация в composition root Host:

```csharp
builder.Services.AddSingleton<INetworkBotBehavior>(
    new PatrolBotBehavior("scanner", "scanner", "Q", node => $"scanner checked {node.Value}"));
```

Для сложного NPC реализуйте интерфейс самостоятельно:

- `Id` — стабильный ASCII-идентификатор (строчные буквы, цифры, `.-_`, до 32 символов), используемый в административном `network_life.spawn.goal` и в поле `life.bots.goal`.
- `Name` и `Signature` — отображаемые поля. Их длины ограничены; запрещены управляющие символы и разделители `|;`.
- `Decide(NetworkBotState, NetworkBotObservation)` — вычисление намерения для одного хода. Снимок `NetworkBotState` неизменяемый. Observation содержит только соседние физические узлы и, для hostile-поведения, вычисленный Host шаг к присутствию оператора.
- `Describe(NodeId)` — короткое сообщение в общем чате и описаниях событий.

`NetworkBotDecisionPolicy` и Host проверяют, что `Next` — текущий либо соседний физический узел. `Engage` допустим только для враждебной стратегии при подтверждённой Host цели на текущем узле. Сетевая сущность не получает прямого доступа к `ServiceRuntime`, DM-объектам, отправке команд или произвольному чтению топологии. Для любых реальных взаимодействий с игровыми устройствами нужно использовать обычный сетевой сервис и физическую доставку пакетов.

Встроенные стратегии — sentinel, watcher, archivist, courier, intruder, saboteur, engineer — регистрируются реестром автоматически. DI передаёт только дополнительные стратегии. Повторное или некорректное имя прерывает запуск, а неизвестный `goal` у административного `spawn` возвращает `unknown_goal`, не подменяя поведение другим. Количество NPC ограничено `NetworkLifeRuntime.MaxBots`. Рестарт транспортной сессии сбрасывает их состояние.

**Граница доверия:** расширение предназначено для доверенного C#-кода сервера, а не песочницы для пользовательских программ. `Decide` выполняется в процессе Host и должен быть быстрым, без блокирующих операций; для пользовательского скриптинга потребуется отдельный интерпретатор с лимитами ресурсов.

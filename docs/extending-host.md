# Расширение Host: модули команд и сетевых контрактов

## Два различных расширения

1. **Сетевой сервис**, то есть взаимодействие двух узлов: реализуйте `IServiceContractModule` в `NetDM.Services`. Зарегистрируйте реализацию в DI как `IServiceContractModule`. `GameNetworkService` собирает реестр контрактов до создания первой сессии и передаёт тот же реестр всем новым сессиям после reconnect/snapshot. Удалённые вызовы **обязательно** должны проходить через `ServiceRuntime.Invoke/Publish` и доставку пакетами.
2. **Локальная команда control plane Host**, например research catalog/journal, диагностика или управление жизненным циклом: реализуйте `IHostIpcModule` в Host. Перечислите операции в `Operations`, укажите `RequiresNetworkReady`, зарегистрируйте в `Program.cs` через `AddSingleton<IHostIpcModule, MyIpcModule>()`. Список операций и обработчики больше не редактируются в большом `PipeHostedService` switch. Конфликт имён останавливает запуск.

## Строгая граница

Ни один `IHostIpcModule` не должен предоставлять обход доставки DM → DM через прямой доступ к мировым объектам. В отличие от команд нового сервиса, локальные IPC-команды управляют только состоянием самого Host (топология, регистрируемые доменные данные, bootstrap). Их вызовы ограничены согласованием v1, owner fence, stream id и порядковым номером команды на уровне `PipeHostedService`.

Готовность сети проверяется перед исполнением модулей, требующих `RequiresNetworkReady`. Изменение или расширение протокола требует собственных контрактов, лимитов payload, версионирования и тестов на недостаточную готовность и повторное исполнение. `PipeHostedService` сохраняет владение сессией и IPC-транспортом.

## Host-provided network services

When a command has physically reached a Host-provided service endpoint, it must be answered by a registered `IHostServiceExtension`, not by a large switch in `ResearchHost`. Implement `ServiceType` and `Answer(GameNetworkService, HostCommand, out Action?)` in a stateless handler, then add one DI registration. `GameNetworkService` derives the Host service type set from the same registrations; an unknown or duplicate name fails closed. The built-in `research.server` is kept local to `ResearchHost`, which owns its durable ledger. The optional post-ack callback is invoked only when the service successfully acknowledges an arrived command. Do not execute DM gameplay effects from these handlers.

using System.Globalization;
using System.Text;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;
using NetDM.Services.Shell;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// network.shell (§14.6, P11): a device runs a pipeline or script. The directory is read through the
/// asker's delivery, so visibility is the asker's and reachability is the device's.
/// </summary>
public static class ShellHost
{
    public const string ServiceType = "network.shell";
    public const int MaxRows = 64, MaxOutputChars = 4096;
    private static readonly ShellLimits Limits = new(MaxRecords: MaxRows);
    private static readonly string ActionKeyNamespace = $"sh.{Guid.NewGuid():N}";

    /// <summary>
    /// Whose line it is and where it runs. A line that arrived as a command reads through its delivery; a
    /// program that outlived its request reads as its owner's endpoint, which must still be alive.
    /// </summary>
    /// <param name="Publisher">The device's own endpoint when the device runs its own line or program: only then
    /// may <c>publish</c> speak for the device's shell service. Null for a line another endpoint asked for.</param>
    /// <param name="InvocationHost">A resident presence's shell endpoint for the physical origin of commands.</param>
    internal sealed record Scope(EndpointKey Initiator, string Instance, NodeId Origin, long? DeliveryId, string KeyPrefix,
        EndpointKey? Publisher = null, EndpointKey? InvocationHost = null);

    internal static JsonElement Answer(GameNetworkService game, GameNetworkService.Session session, HostCommand command, out Action? applied)
    {
        applied = null;
        using var arguments = JsonDocument.Parse(command.Arguments);
        if (command.Command is "scan" or "knowledge" or "enter" or "detach" or "leave" or "presence" or "act" or "move"
            or "preview_move" or "abort_move" or "poll_alarm" or "alarm_action" or "persona" or "life" or "hack_start" or "hack_answer" or "hack_keys" or "hack_use" or "mask")
            return NetworkSpace.Answer(game, session, command, arguments.RootElement);
        if (command.Command is "chat_read" or "chat_send")
            return NetworkSpace.Chat(game, session, command, arguments.RootElement, out applied);
        if (command.Command is "start" or "stop" or "migrate")
            return ShellPrograms.Answer(session, command, arguments.RootElement, out applied);
        var scope = new Scope(command.InitiatorKey, command.Instance, command.ProviderNode, command.DeliveryId,
            $"{ActionKeyNamespace}.{command.DeliveryId.ToString(CultureInfo.InvariantCulture)}",
            command.InitiatorKey == command.ProviderKey ? command.ProviderKey : null);
        var source = new Source(game, session, scope);
        var sink = new Sink(session, scope);
        var result = command.Command == "run_script"
            ? NetShell.RunScript(arguments.RootElement.GetProperty("source").GetString()!, source, Limits, sink)
            : NetShell.Run(arguments.RootElement.GetProperty("line").GetString()!, source, Limits, sink);
        return Output(result);
    }

    /// <summary>A line's result as <c>shell.output</c>.</summary>
    internal static JsonElement Output(ShellResult result)
    {
        var rows = result.Ok ? Format(result.Records) : "";
        if (rows.Length > MaxOutputChars)
            result = new ShellResult(ShellStatus.LimitExceeded, [], result.FuelUsed, "output too long: select fewer fields or records");
        var status = ServiceNames.Code(result.Status);
        return result.Ok
            ? JsonSerializer.SerializeToElement(new { status, count = result.Records.Count, fuel = result.FuelUsed, rows })
            : JsonSerializer.SerializeToElement(new
            {
                status, count = 0, fuel = result.FuelUsed, rows = "", error = Printable(result.Error ?? status), position = result.Position
            });
    }

    /// <summary>Records as <c>name=value,name=value;…</c>; a value never holds the separators.</summary>
    internal static readonly ShellLimits ProgramLimits = Limits;

    internal static string Format(IReadOnlyList<ShellRecord> records)
    {
        var text = new StringBuilder();
        foreach (var record in records)
        {
            if (text.Length > 0)
                text.Append(';');
            var first = true;
            foreach (var (name, value) in record.Fields)
            {
                if (!first)
                    text.Append(',');
                first = false;
                text.Append(name).Append('=').Append(Printable(value switch
                {
                    bool flag => flag ? "true" : "false",
                    IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? ""
                }));
            }
        }
        return text.ToString();
    }

    internal static string Printable(string text)
    {
        var clean = new StringBuilder(Math.Min(text.Length, 200));
        foreach (var c in text)
        {
            if (clean.Length >= 200)
                break;
            clean.Append(char.IsControl(c) || c is ',' or ';' or '=' ? ' ' : c);
        }
        return clean.ToString();
    }

    internal static ShellRecord Record(params (string Name, object Value)[] fields)
    {
        var record = new Dictionary<string, object>(fields.Length, StringComparer.Ordinal);
        foreach (var (name, value) in fields)
            record[name] = value;
        return new ShellRecord(record);
    }

    /// <summary>
    /// <c>invoke</c>: sends a service command under the asker's rights and quotas. Its transport starts at a
    /// resident presence's shell if there is one, or at the asker's endpoint for an ordinary command.
    /// The line does not wait: each command answers with its operation, and the result returns to the asker
    /// over the network like the result of any command. Alone: <c>invoke instance command name=value…</c>;
    /// after a pipeline: <c>… | invoke command name=value…</c> for the <c>instance</c> of each record.
    /// </summary>
    internal sealed class Sink(GameNetworkService.Session session, Scope command) : IShellSink
    {
        private readonly string _keyPrefix = command.KeyPrefix;
        private readonly NetworkInteractionTools _tools = new(session.Services);
        private readonly NetworkToolActor _actor = new(command.Initiator, command.Publisher,
            command.InvocationHost, command.InvocationHost is null ? null : command.Instance,
            command.InvocationHost is null ? null : command.Origin);
        private int _ordinal;
        public List<ShellRecord> Results { get; } = [];

        public bool Knows(string name) => name is "invoke" or "publish" or "watch" or "unwatch";

        internal static string? Validate(string name, ShellArguments arguments, bool piped)
        {
            if (name == "publish")
                return piped && arguments.Positional.Count + arguments.Named.Count == 0 ? null : "… | publish";
            if (name == "unwatch")
                return !piped && arguments.Named.Count == 0 && arguments.Positional is [string key] &&
                    ServiceNames.IsValid(key) ? null : "unwatch <subscription-key>";
            if (name == "watch")
            {
                var validNames = arguments.Named.All(pair => pair switch
                {
                    { Key: "key" or "projection", Value: string value } => ServiceNames.IsValid(value),
                    { Key: "mode", Value: string value } => value is "latest" or "queue",
                    { Key: "pending", Value: long count } => count is >= 1 and <= 16,
                    _ => false
                });
                return validNames && arguments.Positional.Count == (piped ? 0 : 1) &&
                    arguments.Positional.All(word => word is string target && ServiceNames.IsValid(target))
                    ? null : "watch <instance> [-key K] [-projection full] [-mode latest|queue] [-pending 1..16]";
            }
            return name == "invoke" &&
                arguments.Positional.Count == (piped ? 1 : 2) &&
                arguments.Positional.All(word => word is string text && ServiceNames.IsValid(text)) ? null :
                piped ? "… | invoke <command> [name=value…]" : "invoke <instance> <command> [name=value…]";
        }

        public IReadOnlyList<ShellRecord>? Act(string name, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error)
        {
            error = Validate(name, arguments, input is not null);
            if (error is not null)
                return null;
            if (name == "publish")
                return Publish(input!, out error);
            if (name == "unwatch")
            {
                var key = (string)arguments.Positional[0];
                var receipt = _tools.Execute(_actor, new NetworkToolRequest(NetworkToolKind.Unwatch, key));
                ShellRecord[] result = [Record(("key", key),
                    ("status", ServiceNames.Code(receipt.Status)))];
                Results.AddRange(result);
                return result;
            }

            var targets = input is null
                ? [(string)arguments.Positional[0]]
                : input.Select(record => record["instance"] as string).ToArray();
            if (targets.Any(target => !ServiceNames.IsValid(target)))
            {
                error = "every record needs an instance to invoke or watch";
                return null;
            }
            if (name == "watch" && targets.Length > 1 && arguments.Named.ContainsKey("key"))
            {
                error = "multiple watch targets need distinct keys; omit -key";
                return null;
            }

            var done = new List<ShellRecord>(targets.Length);
            for (var i = 0; i < targets.Length; i++)
            {
                var instance = targets[i]!;
                // A remote shell line is bound to the original delivery and may not
                // discover another actor's private service through the Host.
                var descriptor = command.DeliveryId is { } delivery
                    ? session.Services.QueryService(delivery, instance, false)?.Descriptor
                    : session.Services.Describe(command.Initiator, instance);
                if (descriptor is null)
                {
                    done.Add(Record(("instance", instance), ("status", "unknown_service")));
                    continue;
                }
                if (name == "watch")
                {
                    var key = arguments.Named.TryGetValue("key", out var explicitKey)
                        ? (string)explicitKey : instance;
                    var projection = arguments.Named.TryGetValue("projection", out var selected)
                        ? (string)selected : "full";
                    var mode = arguments.Named.TryGetValue("mode", out var modeValue) && (string)modeValue == "queue"
                        ? DeliveryMode.Queue : DeliveryMode.LatestValue;
                    var pending = arguments.Named.TryGetValue("pending", out var requested)
                        ? checked((int)(long)requested) : 1;
                    var receipt = _tools.Execute(_actor, new NetworkToolRequest(NetworkToolKind.Watch,
                        instance, Key: key, Projection: projection, Mode: mode, MaxPending: pending));
                    done.Add(Record(("instance", instance), ("key", key),
                        ("status", receipt.Status is ServiceStatus.Ok or ServiceStatus.Duplicate
                            ? "watching" : ServiceNames.Code(receipt.Status)), ("subscription", receipt.Subscription)));
                    continue;
                }

                var verb = (string)arguments.Positional[^1];
                if (descriptor.ServiceType == ServiceType)
                {
                    done.Add(Record(("instance", instance), ("command", verb),
                        ("status", "forbidden"), ("operation", "")));
                    continue;
                }
                var payload = JsonSerializer.SerializeToElement(arguments.Named);
                var keyForAction = $"{_keyPrefix}.{(_ordinal++).ToString(CultureInfo.InvariantCulture)}";
                var outcome = _tools.Execute(_actor, new NetworkToolRequest(NetworkToolKind.Invoke,
                    instance, verb, keyForAction, payload));
                done.Add(Record(("instance", instance), ("command", verb),
                    ("status", outcome.Status == ServiceStatus.Ok ? "sent" : ServiceNames.Code(outcome.Status)),
                    ("operation", outcome.Operation > 0 ? outcome.Operation.ToString(CultureInfo.InvariantCulture) : "")));
            }
            Results.AddRange(done);
            return done;
        }

        /// <summary>The device publishes only from its own endpoint; all recipients get routed deliveries.</summary>
        private IReadOnlyList<ShellRecord>? Publish(IReadOnlyList<ShellRecord> input, out string? error)
        {
            error = null;
            var rows = Format(input);
            if (rows.Length > MaxOutputChars)
            {
                error = "publication too long: select fewer fields or records";
                return null;
            }
            var payload = JsonSerializer.SerializeToElement(new { status = "ok", count = input.Count, fuel = 0, rows });
            var result = _tools.Execute(_actor, new NetworkToolRequest(NetworkToolKind.Publish,
                command.Instance, Payload: payload, Traffic: PacketTrafficClass.Background));
            ShellRecord[] done = [Record(("instance", command.Instance), ("command", "publish"),
                ("status", result.Status == ServiceStatus.Ok ? "published" : ServiceNames.Code(result.Status)),
                ("operation", ""), ("recipients", result.Recipients))];
            Results.AddRange(done);
            return done;
        }
    }

    internal sealed class Source(GameNetworkService game, GameNetworkService.Session session, Scope command) : IShellSource
    {
        private const int DirectoryCandidates = 2048;

        /// <summary>The whole shell surface, in one place: help, the <c>commands</c> command and the window's
        /// completion list are generated from it, so a new command cannot be undiscoverable.</summary>
        private static readonly (string Name, string Kind, string Usage, string Description)[] Catalogue =
        [
            ("self", "source", "self", "Свой сетевой узел, питание и порты."),
            ("links", "source", "links", "Соседние порты и их состояние."),
            ("nodes", "source", "nodes", "Известные узлы сети (номера для trace и move)."),
            ("services", "source", "services [-type T] [-prefix P] [-hops N]", "Достижимые сервисы; ближайшие первыми."),
            ("api", "source", "api [serviceType]", "API сервисов: типы, команды, схемы запроса и ответа."),
            ("commands", "source", "commands", "Все команды оболочки с примерами."),
            ("route", "source", "route <instance>", "Путь до сервиса: узлы и типы связей."),
            ("trace", "source", "trace <node>", "Путь до узла; ticks — задержка в десятых секунды."),
            ("jobs", "source", "jobs", "Свои операции и их исход."),
            ("result", "source", "result <operation>", "Результат доставленной операции."),
            ("programs", "source", "programs", "Фоновые программы устройства."),
            ("help", "source", "help [basics|services|scripts|programs|api]", "Темы справки."),
            ("where", "filter", "where <field> <op> <value>", "Фильтр: eq, ne, lt, le, gt, ge, like."),
            ("select", "filter", "select <field>...", "Оставить только указанные поля."),
            ("sort", "filter", "sort <field> [-desc]", "Сортировка записей по полю."),
            ("first", "filter", "first [N]", "Первые N записей (по умолчанию 1)."),
            ("last", "filter", "last [N]", "Последние N записей."),
            ("count", "filter", "count", "Сколько записей пришло."),
            ("invoke", "action", "invoke <instance> <command> [name=value…]", "Отправить команду сервису."),
            ("publish", "action", "… | publish", "Опубликовать записи в свой сервис."),
            ("watch", "action", "watch <instance> [-key K] [-projection full] [-mode latest|queue]", "Подписаться на события сервиса через реальную сеть."),
            ("unwatch", "action", "unwatch <subscription-key>", "Отменить подписку своего endpoint.")
        ];
        private static readonly IReadOnlyDictionary<string, ShellRecord[]> Help = new Dictionary<string, ShellRecord[]>(StringComparer.Ordinal)
        {
            ["basics"] =
            [
                HelpRow("basics", "self | select node state", "Проверьте свой сетевой узел и питание.", "self | select node state"),
                HelpRow("basics", "links | where state -eq up", "Посмотрите рабочие порты. Оператор | передаёт записи следующей команде.", "links | where state -eq up"),
                HelpRow("basics", "trace <node>", "Проверьте путь до известного номера узла. ticks — задержка в десятых секунды.")
            ],
            ["services"] =
            [
                HelpRow("services", "services [-type T] [-prefix P] [-hops N]", "Найдите достижимые сервисы. Сначала показаны ближайшие.", "services -type atmos.alarm -hops 4 | first 3"),
                HelpRow("services", "route <instance>", "Посмотрите узлы и типы связей на пути до сервиса."),
                HelpRow("services", "invoke <instance> <command> [параметры]", "Отправьте команду. Для чтения атмосферы добавьте аргумент reason со значением inspect."),
                HelpRow("services", "watch <instance> [-key K] [-projection full] [-mode latest|queue]", "Подписывайтесь на события устройств; доставка идёт во входящие сообщения endpoint.", "watch atmos.o2_sensor -key oxygen -projection panel.v1"),
                HelpRow("services", "unwatch <subscription-key>", "Отменить именованную подписку.", "unwatch oxygen"),
                HelpRow("services", "jobs | first 5", "Проверьте исход операции по полям outcome и result.", "jobs | first 5"),
                HelpRow("services", "result <operation>", "Получите доставленные показания атмосферной сигнализации по номеру своей операции.")
            ],
            ["scripts"] =
            [
                HelpRow("scripts", "where | select | sort | first | last | count", "Фильтруйте записи оператором where. Выбирайте поля с select.", "links | where state -eq up | count"),
                HelpRow("scripts", "$имя ← конвейер", "Чтобы присвоить переменной записи используйте знак равенства. Имя задаётся латиницей."),
                HelpRow("scripts", "if строка … end", "Блок выполняется если условие вернуло хотя бы одну запись.")
            ],
            ["programs"] =
            [
                HelpRow("programs", "programs", "Просмотрите состояние фоновых программ на устройстве.", "programs"),
                HelpRow("programs", "every <секунды>", "Первая строка фонового скрипта задаёт таймер от 10 секунд. Запуск и остановка доступны в редакторе."),
                HelpRow("programs", "on строка … end", "Правило срабатывает когда источник меняется и условие даёт записи."),
                HelpRow("programs", "wait <секунды> / $state", "Приостановите программу или сохраните записи между её проходами.")
            ],
            ["api"] =
            [
                HelpRow("api", "api", "Какие сервисы доступны и какие команды они принимают."),
                HelpRow("api", "api <serviceType>", "Команды сервиса: аргументы (args), поля ответа (returns), схемы, host/transient."),
                HelpRow("api", "api <serviceType> <command>", "Все аргументы и поля результата: обязательность, диапазоны, значения enum."),
                HelpRow("api", "commands", "Все команды оболочки с примерами (этот список используется и для Tab)."),
                HelpRow("api", "nodes", "Номера известных узлов: для trace, move и маршрутов.")
            ]
        };

        private static ShellRecord HelpRow(string topic, string syntax, string description, string example = "") =>
            Record(("topic", topic), ("syntax", syntax), ("description", description), ("example", example));

        private int _graphQueriesRemaining = 2;
        /// <summary>Sources a line has read: a rule is evaluated again only when one of them changed.</summary>
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        internal static string? Validate(string name, ShellArguments args, bool variable = false)
        {
            if (variable)
                return args.Positional.Count + args.Named.Count == 0 ? null : "a variable takes no arguments";
            if (name == "help")
                return args.Named.Count == 0 && (args.Positional.Count == 0 || args.Positional is [string topic] && Help.ContainsKey(topic))
                    ? null : "help [basics|services|scripts|programs]";
            if (name == "commands")
                return args.Positional.Count + args.Named.Count == 0 ? null : "commands takes no arguments";
            if (name == "api")
                return args.Named.Count == 0 && args.Positional switch
                {
                    [] => true,
                    [string type] => ServiceNames.IsValid(type),
                    [string type, string command] => ServiceNames.IsValid(type) && ServiceNames.IsValid(command),
                    _ => false
                } ? null : "api [serviceType [command]]";
            if (name == "nodes")
                return args.Positional.Count + args.Named.Count == 0 ? null : "nodes takes no arguments";
            if (name == "result")
                return args.Named.Count == 0 && args.Positional is [long and > 0] ? null : "result <operation>";
            if (name == "services")
                return args.Positional.Count == 0 && args.Named.All(pair => pair switch
                {
                    { Key: "type" or "prefix", Value: string text } => ServiceNames.IsValid(text),
                    { Key: "hops", Value: long hops } => hops is >= 0 and <= QueryHost.MaxHopsFilter,
                    _ => false
                }) ? null : "services [-type <serviceType>] [-prefix <instance prefix>] [-hops <0-64>]";
            if (name == "trace")
                return args.Named.Count == 0 && args.Positional.Count == 1 && args.Positional[0] is long and > 0 ? null : "trace <node>";
            if (name == "route")
                return args.Named.Count == 0 && args.Positional.Count == 1 && args.Positional[0] is string instance &&
                    ServiceNames.IsValid(instance) ? null : "route <instance>";
            if (name is "programs" or "links" or "self" or "jobs")
                return args.Positional.Count + args.Named.Count == 0 ? null : $"{name} takes no arguments";
            return "unknown command";
        }

        /// <summary>Known wire ids for `nodes` and for the window's Tab completion.</summary>
        private IReadOnlyList<string> KnownNodes() =>
            NetworkSpace.KnownNodeIds(command.Initiator, session.Packets.ReceiptStreamId, DirectoryCandidates);

        /// <summary>`api [type [command]]`: what the reachable services offer, without opening their code.
        /// No type groups the directory; a type lists commands with their arguments; a command lists every
        /// argument and result field with type, requiredness, range or enum values.</summary>
        private IReadOnlyList<ShellRecord> DiscoverApi(ShellArguments arguments, out string? error)
        {
            error = null;
            var type = arguments.Positional is [string wanted, ..] ? wanted : null;
            var commandName = arguments.Positional is [_, string wantedCommand] ? wantedCommand : null;
            var registry = session.Services.Schemas;
            if (type is not null && commandName is not null)
            {
                if (registry.FindCommand(type, commandName) is not { } definition)
                {
                    error = $"у сервиса {type} нет команды {commandName}";
                    return [];
                }
                return definition.Arguments.Fields
                    .Select(field => ApiFieldRow("request", field))
                    .Concat(definition.Result.Fields.Select(field => ApiFieldRow("result", field)))
                    .ToArray();
            }
            if (type is not null)
            {
                return registry.CommandNames(type).Select(name =>
                {
                    var definition = registry.FindCommand(type, name);
                    return Record(("type", type), ("command", name),
                        ("request", definition?.Arguments.Id ?? ""), ("result", definition?.Result.Id ?? ""),
                        ("args", ApiFields(definition?.Arguments)), ("returns", ApiFields(definition?.Result)),
                        ("host", definition?.HostHandled == true ? "yes" : "no"),
                        ("transient", definition?.Transient == true ? "yes" : "no"));
                }).ToArray();
            }
            var slice = command.DeliveryId is { } delivery
                ? session.Services.QueryDirectory(delivery, null, null, DirectoryCandidates)?.Slice
                : session.Services.QueryDirectory(command.Initiator, null, null, DirectoryCandidates);
            var entries = slice?.Entries ?? [];
            return entries.GroupBy(entry => entry.ServiceType, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => Record(("type", group.Key), ("instances", (long)group.Count()),
                    ("commands", string.Join(", ", registry.CommandNames(group.Key))))).ToArray();
        }

        /// <summary>One argument field of `api type command`: what a caller must or may send.</summary>
        private static ShellRecord ApiFieldRow(string direction, SchemaField field) =>
            Record(("how", direction), ("field", field.Name), ("type", ApiTypeName(field.Type)),
                ("required", field.Required ? "yes" : "no"), ("spec", ApiFieldSpec(field)));

        /// <summary>A compact human line for all fields: name, type, ?/! and limits.</summary>
        private static string ApiFields(SchemaDefinition? schema) =>
            schema is null ? "" : string.Join(", ", schema.Fields.Select(field =>
                $"{field.Name}:{ApiTypeName(field.Type)}{(field.Required ? "!" : "?")}{ApiFieldSpec(field)}"));

        private static string ApiTypeName(SchemaFieldType type) => type switch
        {
            SchemaFieldType.Text => "text",
            SchemaFieldType.MultilineText => "multiline",
            SchemaFieldType.Integer => "integer",
            SchemaFieldType.Number => "number",
            SchemaFieldType.Boolean => "boolean",
            SchemaFieldType.Enum => "enum",
            _ => type.ToString().ToLowerInvariant()
        };

        private static string ApiFieldSpec(SchemaField field) => field.Type switch
        {
            SchemaFieldType.Enum when field.Values is { } values => $"={string.Join('|', values)}",
            SchemaFieldType.Integer or SchemaFieldType.Number => $" [{field.Min}..{field.Max}]",
            SchemaFieldType.Text or SchemaFieldType.MultilineText => $" ≤{field.MaxLength}",
            _ => ""
        };

        public IReadOnlyList<ShellRecord>? Read(string name, ShellArguments arguments, int maxRecords, out string? error)
        {
            error = Validate(name, arguments);
            if (error is not null)
            {
                if (error == "unknown command")
                    error = null;
                return null;
            }
            Names.Add(name);
            if (name == "help")
                return arguments.Positional is [string topic] ? Help[topic] :
                [
                    HelpRow("basics", "help basics", "Узел и его соединения", "help basics"),
                    HelpRow("services", "help services", "Поиск сервисов и результаты команд", "help services"),
                    HelpRow("api", "help api", "API сервисов, команды и узлы", "api"),
                    HelpRow("scripts", "help scripts", "Конвейеры и условия", "help scripts"),
                    HelpRow("programs", "help programs", "Таймеры и фоновые правила", "help programs")
                ];
            if (name == "commands")
                return Catalogue.Select(row => Record(("name", row.Name), ("kind", row.Kind), ("usage", row.Usage),
                    ("description", row.Description))).ToArray();
            if (name == "nodes")
                return KnownNodes().Take(maxRecords).Select(node => Record(("node", node))).ToArray();
            if (name == "api")
                return DiscoverApi(arguments, out error);
            var world = session.Medium.Snapshot;
            var origin = command.Origin;
            switch (name)
            {
                case "result":
                    var id = (long)arguments.Positional[0];
                    if (session.Services.ShellResultPayload(command.Initiator, id) is not { } result)
                        return Fail("Нет доставленного результата. Проверьте jobs и номер операции.", out error);
                    return [new ShellRecord(result.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.ValueKind switch
                    {
                        JsonValueKind.True => (object)true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when field.Value.TryGetInt64(out var integer) => integer,
                        JsonValueKind.Number => field.Value.GetDouble(),
                        _ => field.Value.GetString() ?? ""
                    }))];
                case "programs":
                    return ShellPrograms.List(session, command);
                case "jobs":
                    return QueryHost.Jobs(session.Services, command.Initiator, maxRecords, command.DeliveryId is null ? null : command.Instance)
                        .Select(job => Record(("operation", job.Operation), ("instance", job.Instance), ("command", job.Command),
                            ("outcome", job.Outcome), ("result", job.Result), ("reason", job.Reason))).ToArray();
                case "route":
                    var routed = (string)arguments.Positional[0];
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services, trace and route share two searches)", out error);
                    var service = command.DeliveryId is { } routeDelivery
                        ? session.Services.QueryService(routeDelivery, routed, false)?.Target
                        : session.Services.DiscoverableNode(command.Initiator, routed);
                    if (service is not { } serviceNode)
                        return [];
                    var path = NetworkDiagnostics.Route(world, origin, serviceNode, QueryHost.ReachBudget);
                    if (path.State != NetworkDiagnostics.Reachable)
                        return [Record(("hop", 0L), ("node", ""), ("link", path.State))];
                    return path.Nodes.Select((hop, index) => Record(("hop", (long)index), ("node", hop.Node.Value.ToString(CultureInfo.InvariantCulture)),
                        ("link", index == 0 ? "start" : hop.Radio ? "radio" : "cable"))).ToArray();
                case "services":
                    var type = arguments.Named.GetValueOrDefault("type") as string;
                    var prefix = arguments.Named.GetValueOrDefault("prefix") as string;
                    var maxHops = arguments.Named.GetValueOrDefault("hops") is long hopLimit ? hopLimit : long.MaxValue;
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services and trace share two searches)", out error);
                    var slice = command.DeliveryId is { } delivery
                        ? session.Services.QueryDirectory(delivery, type, null, DirectoryCandidates, prefix)?.Slice
                        : session.Services.QueryDirectory(command.Initiator, type, null, DirectoryCandidates, prefix);
                    if (slice is null)
                        return [];
                    var query = (Slice: slice, Origin: origin);
                    if (query.Slice.More)
                        return Fail("too many services: narrow with -type or -prefix", out error);
                    var targets = query.Slice.Entries.Select(entry => entry.Node).Where(node => node != origin).ToHashSet();
                    var reached = SharedMediumNetwork.ReachAll(world, origin, targets, QueryHost.ReachBudget, out var exhausted);
                    if (exhausted)
                        return Fail("the network is too large to search from here", out error);
                    return query.Slice.Entries
                        .Where(entry => entry.Node == origin || (reached.TryGetValue(entry.Node, out var near) && near.Hops <= maxHops))
                        .Select(entry => (Entry: entry, Hops: entry.Node == origin ? 0 : reached[entry.Node].Hops))
                        .OrderBy(entry => entry.Hops)
                        .Take(maxRecords)
                        .Select(entry => Record(("instance", entry.Entry.Instance), ("type", entry.Entry.ServiceType),
                            ("hops", (long)entry.Hops))).ToArray();
                case "links":
                    return NetworkDiagnostics.LinkState(world, game.Wiring, origin) is { } ports
                        ? ports.Neighbors.Select(link => Record(("node", link.Node.Value.ToString(CultureInfo.InvariantCulture)), ("state", link.State))).ToArray()
                        : [];
                case "self":
                    return NetworkDiagnostics.LinkState(world, game.Wiring, origin) is { } own
                        ? [Record(("node", origin.Value.ToString(CultureInfo.InvariantCulture)), ("state", own.State), ("wired", (long)own.Wired),
                            ("wiredUp", (long)own.WiredUp), ("radio", own.Radio), ("devices", (long)own.Devices))]
                        : [];
                case "trace":
                    var target = (long)arguments.Positional[0];
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services and trace share two searches)", out error);
                    var trace = NetworkDiagnostics.Trace(world, origin, new NodeId((ulong)target), QueryHost.ReachBudget);
                    return [Record(("node", target.ToString(CultureInfo.InvariantCulture)), ("state", trace.State), ("hops", (long)trace.Hops),
                        ("radioHops", (long)trace.RadioHops), ("ticks", (long)trace.Ticks))];
                default:
                    return null;
            }
        }

        private static IReadOnlyList<ShellRecord>? Fail(string message, out string? error)
        {
            error = message;
            return null;
        }
    }
}

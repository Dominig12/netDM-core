using System.Globalization;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;
using NetDM.Services.Shell;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// Programs of the network shell (§14.6): a script that stays on a device after the request that started
/// it. It keeps acting as the endpoint that started it, with that endpoint's rights and quotas, and reads
/// the network from the device it runs on.
///
/// A program is a list of phases separated by lines <c>wait</c>. After a phase the program waits until the
/// commands that phase sent have an outcome (or a deadline passes); the next phase then finds them in
/// <c>$result</c> with their <c>outcome</c>. A first line <c>on &lt;pipeline&gt;</c> makes the program a rule:
/// the read-only pipeline is evaluated about once a second, and when its records differ from the last
/// evaluation the phases run with the new records in <c>$event</c>. Without <c>on</c> the program runs once.
///
/// It lives in the game runtime it was started in: a new session, a device that left the directory or an
/// owner whose endpoint is gone ends it. A device without power suspends it. Everything is bounded: programs
/// per device and in total, evaluations per second, fuel per evaluation, the wait and the runs of a rule.
/// All calls come from the one loop that answers Host commands, so there is no locking here.
/// </summary>
public static class ShellPrograms
{
    public const int MaxPerDevice = 4, MaxTotal = 64, MaxPhases = 4, MaxRuns = 100;
    public const int EvaluateEveryMs = 1000, PassEveryMs = 250, WaitDeadlineMs = 30_000;
    /// <summary>
    /// What all programs of one device may spend in a minute: fuel of rule evaluations and phases, and
    /// commands they try to send. A device over its budget holds its programs ("throttled") until the
    /// minute ends; nothing is dropped. A rule waits <see cref="RuleCooldownMs"/> after it fired, so changes
    /// in the meantime are seen together by its next evaluation.
    /// </summary>
    public const int DeviceFuelPerWindow = 16_384, DeviceActionsPerWindow = 32, BudgetWindowMs = 60_000, RuleCooldownMs = 5_000;

    private sealed class Budget
    {
        public long WindowStart = long.MinValue / 2, Fuel;
        public int Actions;
    }

    private static readonly Dictionary<string, Budget> Budgets = new(StringComparer.Ordinal);

    private static Budget BudgetOf(string instance, long now)
    {
        if (!Budgets.TryGetValue(instance, out var budget))
            Budgets[instance] = budget = new Budget();
        if (now - budget.WindowStart >= BudgetWindowMs)
        {
            budget.WindowStart = now;
            budget.Fuel = 0;
            budget.Actions = 0;
        }
        return budget;
    }

    private static bool Afford(Program program, long now)
    {
        var budget = BudgetOf(program.Instance, now);
        return budget.Fuel < DeviceFuelPerWindow && budget.Actions < DeviceActionsPerWindow;
    }

    private static void Charge(Program program, long fuel, int actions)
    {
        var budget = BudgetOf(program.Instance, Environment.TickCount64);
        budget.Fuel += fuel;
        budget.Actions += actions;
        program.Fuel += fuel;
    }

    /// <summary>Holds a program until its device's budget window ends; it continues where it stood.</summary>
    private static void Throttle(Program program, long now)
    {
        if (program.State != "throttled")
            program.ThrottledFrom = program.State;
        program.State = "throttled";
        var budget = BudgetOf(program.Instance, now);
        program.Reason = $"device budget spent: {budget.Fuel} fuel, {budget.Actions} commands this minute";
    }

    private sealed class Program
    {
        public int Id;
        public string Name = "", Instance = "", State = "running", Reason = "", Resume = "running", ThrottledFrom = "running";
        public EndpointKey Owner, Provider;
        public string? Rule;
        public string[] Phases = [];
        public string? LastEvent;
        public int Phase, Runs;
        public long NextEvaluation, WaitUntil, SuspendedAt, Fuel;
        public long[] Waiting = [];
        public IReadOnlyList<ShellRecord> Sent = [], Event = [], Result = [];
        public bool Finished => State is "done" or "faulted" or "stopped";
        /// <summary>Restored or carried over a new runtime: its endpoints may still be on their way back.</summary>
        public bool Detached;
        public long DetachedSince;
        /// <summary>Set on disk before a phase runs and cleared after: a phase is never run twice.</summary>
        public bool InPhase;
    }

    // ---- Persistence ------------------------------------------------------------------------
    //
    // Programs are written to programs-<round>.json next to the research journal whenever they change, and
    // read back when a Host process opens the same round. A new runtime (a resync or a Host restart) keeps
    // them; their endpoints get ReattachGraceMs to come back. Commands of the old runtime have no outcome
    // in the new one: a wait across it ends with "unknown". A phase interrupted by a crash is not run again
    // (at most once): its program continues after it with what is known.

    public const int ReattachGraceMs = 60_000;
    private const int FileVersion = 1;

    private sealed record SavedProgram(int Id, string Name, string Instance, string OwnerId, ulong OwnerGeneration, string ProviderId,
        ulong ProviderGeneration, string? Rule, string[] Phases, string? LastEvent, int Phase, int Runs, string State, string Resume,
        string ThrottledFrom, string Reason, long Fuel, bool InPhase, List<Dictionary<string, JsonElement>> Sent,
        List<Dictionary<string, JsonElement>> Event);

    private sealed record SavedFile(int Version, Guid Round, int NextId, List<SavedProgram> Programs);

    private static (string Directory, Guid Round)? _pendingBind;
    private static readonly object BindGate = new();
    private static string? _path;
    private static Guid _round;
    private static string? _written;

    /// <summary>The last failure to write the programs file, or null; programs keep running in memory.</summary>
    public static string? SaveError { get; private set; }

    /// <summary>Called when a session names its round; applied by the loop that runs the programs.</summary>
    public static void Bind(string directory, Guid round)
    {
        lock (BindGate)
            _pendingBind = (directory, round);
    }

    private static void ApplyBind()
    {
        (string Directory, Guid Round)? bind;
        lock (BindGate)
        {
            bind = _pendingBind;
            _pendingBind = null;
        }
        if (bind is not { } target || target.Round == _round)
            return;
        _round = target.Round;
        _path = Path.Combine(target.Directory, $"programs-{target.Round:N}.json");
        Programs.Clear();
        Budgets.Clear();
        _nextId = 0;
        _written = null;
        try
        {
            if (!File.Exists(_path))
                return;
            var saved = JsonSerializer.Deserialize<SavedFile>(File.ReadAllBytes(_path));
            if (saved is null || saved.Version != FileVersion || saved.Round != target.Round)
                return;
            _nextId = saved.NextId;
            var now = Environment.TickCount64;
            foreach (var entry in saved.Programs.Take(MaxTotal))
                Programs.Add(Restore(entry, now));
            _written = File.ReadAllText(_path);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveError = exception.Message; // An unreadable file starts the round without programs; it is overwritten.
        }
    }

    private static Program Restore(SavedProgram saved, long now)
    {
        var program = new Program
        {
            Id = saved.Id, Name = saved.Name, Instance = saved.Instance, Owner = new EndpointKey(saved.OwnerId, saved.OwnerGeneration),
            Provider = new EndpointKey(saved.ProviderId, saved.ProviderGeneration), Rule = saved.Rule, Phases = saved.Phases,
            LastEvent = saved.LastEvent, Phase = saved.Phase, Runs = saved.Runs, State = saved.State, Resume = saved.Resume,
            ThrottledFrom = saved.ThrottledFrom, Reason = saved.Reason, Fuel = saved.Fuel, Sent = saved.Sent.Select(Record).ToArray(),
            Event = saved.Event.Select(Record).ToArray(), Detached = true, SuspendedAt = now
        };
        if (saved.InPhase)
        {
            // The Host stopped inside this phase: what it sent is unknown, and it is not sent again.
            program.Sent = [];
            program.Reason = "restored after an interrupted phase";
            if (program.Phase + 1 < program.Phases.Length)
                program.State = "waiting";
            else if (program.Rule is null)
                program.State = "done";
            else
                program.State = "watching";
        }
        Forget(program);
        return program;
    }

    /// <summary>A new runtime: operations of the old one are gone, so a wait ends with unknown outcomes.</summary>
    private static void Forget(Program program)
    {
        program.Waiting = program.Sent.Select(_ => 0L).ToArray();
        program.WaitUntil = 0;
        program.NextEvaluation = 0;
        program.Detached = true;
        program.DetachedSince = 0;
    }

    private static ShellRecord Record(Dictionary<string, JsonElement> fields) => new(fields.ToDictionary(pair => pair.Key, pair => pair.Value.ValueKind switch
    {
        JsonValueKind.Number when pair.Value.TryGetInt64(out var whole) => (object)whole,
        JsonValueKind.Number => pair.Value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => pair.Value.GetString() ?? ""
    }, StringComparer.Ordinal));

    private static List<Dictionary<string, JsonElement>> Fields(IReadOnlyList<ShellRecord> records) =>
        records.Select(record => record.Fields.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal)).ToList();

    /// <summary>Writes the programs when they differ from what is on disk: temporary file, flush, rename.</summary>
    private static void Save()
    {
        if (_path is null)
            return;
        var file = new SavedFile(FileVersion, _round, _nextId, Programs.Where(program => !program.Finished).Select(program => new SavedProgram(
            program.Id, program.Name, program.Instance, program.Owner.Id, program.Owner.Generation, program.Provider.Id, program.Provider.Generation,
            program.Rule, program.Phases, program.LastEvent, program.Phase, program.Runs, program.State, program.Resume, program.ThrottledFrom,
            program.Reason, program.Fuel, program.InPhase, Fields(program.Sent), Fields(program.Event))).ToList());
        var text = JsonSerializer.Serialize(file);
        if (text == _written)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
            _written = text;
            SaveError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveError = exception.Message;
        }
    }

    private static readonly List<Program> Programs = [];
    private static Guid _stream;
    private static int _nextId;
    private static long _nextPass;

    private static void Sync(GameNetworkService.Session session)
    {
        ApplyBind();
        if (_stream == session.Packets.ReceiptStreamId)
            return;
        // A new runtime: the programs stay, their endpoints come back with the world's facts.
        _stream = session.Packets.ReceiptStreamId;
        Budgets.Clear();
        foreach (var program in Programs.Where(program => !program.Finished))
            Forget(program);
    }

    internal static JsonElement Answer(GameNetworkService.Session session, HostCommand command, JsonElement arguments, out Action? applied)
    {
        applied = null;
        Sync(session);
        if (command.Command == "stop")
        {
            var id = (int)arguments.GetProperty("program").GetDouble();
            var found = Programs.Find(program => program.Id == id && program.Instance == command.Instance &&
                program.Owner == command.InitiatorKey && program.Provider == command.ProviderKey);
            if (found is null)
                return JsonSerializer.SerializeToElement(new { status = "unknown_program" });
            applied = () => Programs.Remove(found);
            return JsonSerializer.SerializeToElement(new { status = "stopped", program = id });
        }
        var name = arguments.GetProperty("name").GetString() ?? "";
        var text = arguments.GetProperty("source").GetString() ?? "";
        object Refuse(string status, string error, int position = 0) => new { status, error = ShellHost.Printable(error), position };
        if (!ServiceNames.IsValid(name))
            return JsonSerializer.SerializeToElement(Refuse("invalid_argument", "a program needs a name of letters, digits, '.', '_' or '-'"));
        if (Programs.Count(program => !program.Finished) >= MaxTotal ||
            Programs.Count(program => !program.Finished && program.Instance == command.Instance) >= MaxPerDevice)
            return JsonSerializer.SerializeToElement(Refuse("limit_exceeded", $"a device holds at most {MaxPerDevice} programs"));

        // "on <pipeline>" is the first line; "wait" on a line of its own separates the phases.
        var lines = text.Split('\n');
        string? rule = null;
        var offset = 0;
        var first = lines[0].TrimStart();
        if (first.StartsWith("on", StringComparison.Ordinal) && (first.Length == 2 || char.IsWhiteSpace(first[2])))
        {
            rule = first[2..].Trim();
            offset = lines[0].Length + 1;
            lines = lines[1..];
        }
        var phases = new List<(string Source, int Offset)>();
        var current = new List<string>();
        var phaseOffset = offset;
        foreach (var line in lines)
        {
            if (line.Trim() == "wait")
            {
                phases.Add((string.Join('\n', current), phaseOffset));
                current.Clear();
                phaseOffset = offset + line.Length + 1;
            }
            else
                current.Add(line);
            offset += line.Length + 1;
        }
        phases.Add((string.Join('\n', current), phaseOffset));
        if (phases.Count > MaxPhases)
            return JsonSerializer.SerializeToElement(Refuse("limit_exceeded", $"a program waits at most {MaxPhases - 1} times"));

        // The whole program is checked now, against a network that answers nothing: no line runs at start.
        var silent = new Dry();
        var presets = new Dictionary<string, IReadOnlyList<ShellRecord>>(StringComparer.Ordinal) { ["event"] = [], ["result"] = [] };
        if (rule is not null)
        {
            var checkedRule = rule.Length == 0
                ? new ShellResult(ShellStatus.ParseError, [], 0, "on needs a pipeline")
                : NetShell.RunScript("if " + rule + "\nend", silent, ShellHost.ProgramLimits, silent,
                    validateSource: (commandName, args, variable) => commandName == "programs"
                        ? "programs cannot be a rule source" : ShellHost.Source.Validate(commandName, args, variable));
            if (!checkedRule.Ok)
                return JsonSerializer.SerializeToElement(Refuse(Code(checkedRule.Status), checkedRule.Error ?? "", Math.Max(0, checkedRule.Position - 1)));
        }
        foreach (var (phase, start) in phases)
        {
            var checkedPhase = NetShell.RunScript(phase, silent, ShellHost.ProgramLimits, silent, presets,
                ShellHost.Source.Validate, ShellHost.Sink.Validate);
            if (!checkedPhase.Ok)
                return JsonSerializer.SerializeToElement(Refuse(Code(checkedPhase.Status), checkedPhase.Error ?? "", start + checkedPhase.Position));
        }
        var started = new Program
        {
            Id = ++_nextId, Name = name, Instance = command.Instance, Owner = command.InitiatorKey, Provider = command.ProviderKey,
            Rule = rule, Phases = phases.Select(phase => phase.Source).ToArray(),
            State = rule is null ? "running" : "watching"
        };
        applied = () =>
        {
            Programs.RemoveAll(program => program.Finished && program.Instance == command.Instance && program.Owner == command.InitiatorKey);
            if (Programs.Count == MaxTotal)
                Programs.RemoveAt(Programs.FindIndex(program => program.Finished));
            Programs.Add(started);
        };
        return JsonSerializer.SerializeToElement(new { status = "started", program = started.Id });
    }

    private static string Code(ShellStatus status) => status switch
    {
        ShellStatus.UnknownCommand => "unknown_command",
        ShellStatus.LimitExceeded or ShellStatus.FuelExhausted => "limit_exceeded",
        ShellStatus.ParseError => "parse_error",
        _ => "invalid_argument"
    };

    /// <summary>A network that answers nothing and sends nothing: it only lets a script be checked.</summary>
    private sealed class Dry : IShellSource, IShellSink
    {
        public IReadOnlyList<ShellRecord>? Read(string command, ShellArguments arguments, int maxRecords, out string? error)
            => throw new InvalidOperationException("Validation must not read from the network.");

        public bool Knows(string command) => command == "invoke";

        public IReadOnlyList<ShellRecord>? Act(string command, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error)
            => throw new InvalidOperationException("Validation must not send commands.");
    }

    /// <summary>The asker's own programs on this device.</summary>
    internal static IReadOnlyList<ShellRecord> List(GameNetworkService.Session session, ShellHost.Scope scope)
    {
        Sync(session);
        return Programs.Where(program => program.Instance == scope.Instance && program.Owner == scope.Initiator)
            .Select(program => ShellHost.Record(("program", (long)program.Id), ("name", program.Name), ("state", program.State),
                ("runs", (long)program.Runs), ("fuel", program.Fuel), ("reason", program.Reason))).ToArray();
    }

    /// <summary>One bounded pass over the programs: finished waits continue, due rules are evaluated.</summary>
    public static void Tick(GameNetworkService game)
    {
        var now = Environment.TickCount64;
        if (now < _nextPass)
            return;
        _nextPass = now + PassEveryMs;
        var session = game.CaptureSession();
        Sync(session);
        if (Programs.Count == 0)
        {
            Save(); // A removed last program leaves an empty file, not a stale one.
            return;
        }
        var sent = false;
        for (var index = Programs.Count - 1; index >= 0; index--)
        {
            var program = Programs[index];
            if (session.Services.ResidentCommandNode(program.Owner, program.Provider, program.Instance) is not { } origin)
            {
                // A program carried into a new runtime waits for its endpoints; any other leaves with them.
                if (program.Detached && !program.Finished)
                {
                    if (program.DetachedSince == 0)
                        program.DetachedSince = now;
                    if (now - program.DetachedSince < ReattachGraceMs)
                        continue;
                }
                Programs.RemoveAt(index);
                continue;
            }
            program.Detached = false;
            program.DetachedSince = 0;
            if (program.Finished)
                continue;
            if (game.CapturePowerState(session) is not { } power)
                break;
            if (!power.World.Physical.Nodes.ContainsKey(origin) || !power.Wiring.CanExecute(origin))
            {
                if (program.State != "suspended")
                {
                    program.Resume = program.State;
                    program.SuspendedAt = now;
                }
                program.State = "suspended"; // No power, no execution; waits and rules resume with the power.
                continue;
            }
            if (program.State == "suspended")
            {
                if (program.Resume == "waiting")
                    program.WaitUntil += now - program.SuspendedAt;
                program.State = program.Resume;
            }
            if (program.State == "throttled")
            {
                if (!Afford(program, now))
                    continue;
                program.State = program.ThrottledFrom;
                program.Reason = "";
            }
            if ((program.State == "running" || (program.State == "watching" && now >= program.NextEvaluation)) && !Afford(program, now))
            {
                Throttle(program, now);
                continue;
            }
            var scope = new ShellHost.Scope(program.Owner, program.Instance, origin, null,
                $"pg.{_stream:N}.{program.Id.ToString(CultureInfo.InvariantCulture)}.{program.Runs.ToString(CultureInfo.InvariantCulture)}.{program.Phase.ToString(CultureInfo.InvariantCulture)}");
            switch (program.State)
            {
                case "watching" when now >= program.NextEvaluation:
                    program.NextEvaluation = now + EvaluateEveryMs;
                    var seen = NetShell.Run(program.Rule!, new ShellHost.Source(game, session, scope), ShellHost.ProgramLimits);
                    Charge(program, seen.FuelUsed, 0);
                    if (!seen.Ok)
                    {
                        program.Reason = ShellHost.Printable(seen.Error ?? "rule failed"); // The rule keeps watching: a network may heal.
                        break;
                    }
                    var text = ShellHost.Format(seen.Records);
                    var first = program.LastEvent is null;
                    if (text == program.LastEvent)
                        break;
                    program.LastEvent = text;
                    program.Reason = "";
                    if (first)
                        break; // The first evaluation is what the rule compares with.
                    if (program.Runs >= MaxRuns)
                    {
                        Finish(program, "faulted", "run budget spent");
                        break;
                    }
                    program.Event = seen.Records;
                    program.Result = [];
                    program.Phase = 0;
                    sent |= RunPhase(game, session, program, scope);
                    break;
                case "running":
                    sent |= RunPhase(game, session, program, scope);
                    break;
                case "waiting":
                    var outcomes = program.Waiting.Select(id => session.Services.Operation(program.Owner, id)).ToArray();
                    var late = now >= program.WaitUntil;
                    if (!late && outcomes.Any(outcome => outcome.Status == ServiceStatus.Ok && outcome.Operation!.Outcome == CommandOutcome.Pending))
                        break;
                    if (!Afford(program, now))
                    {
                        Throttle(program, now); // The outcomes stay with the operations: they are read again on resume.
                        break;
                    }
                    program.Result = program.Sent.Select((record, index) =>
                    {
                        var fields = new Dictionary<string, object>(record.Fields, StringComparer.Ordinal);
                        var outcome = index < outcomes.Length ? outcomes[index] : default;
                        fields["outcome"] = outcome.Status != ServiceStatus.Ok || outcome.Operation is null ? "unknown"
                            : outcome.Operation.Outcome == CommandOutcome.Pending ? "timeout" : ServiceNames.Code(outcome.Operation.Outcome);
                        return new ShellRecord(fields);
                    }).ToArray();
                    program.Phase++;
                    program.State = "running";
                    sent |= RunPhase(game, session, program, scope with { KeyPrefix = scope.KeyPrefix + "w" });
                    break;
            }
        }
        if (sent)
            game.DrainSubmissions();
        Save();
    }

    /// <summary>Runs the current phase; returns whether it sent commands. Then the program waits, finishes or watches again.</summary>
    private static bool RunPhase(GameNetworkService game, GameNetworkService.Session session, Program program, ShellHost.Scope scope)
    {
        if (program.Phase == 0)
            program.Runs++;
        var presets = new Dictionary<string, IReadOnlyList<ShellRecord>>(StringComparer.Ordinal) { ["event"] = program.Event, ["result"] = program.Result };
        var sink = new ShellHost.Sink(session, scope);
        program.InPhase = true;
        Save();
        var result = NetShell.RunScript(program.Phases[program.Phase], new ShellHost.Source(game, session, scope), ShellHost.ProgramLimits, sink, presets);
        program.InPhase = false;
        var sent = sink.Results.Any(record => record["status"] as string == "sent");
        // Every command a phase tried counts, sent or refused: trying is what floods.
        Charge(program, result.FuelUsed, sink.Results.Count);
        if (!result.Ok)
        {
            Finish(program, "faulted", result.Error ?? ServiceNames.Code(result.Status));
            return sent;
        }
        if (program.Phase + 1 < program.Phases.Length)
        {
            // "wait": the commands of this phase, sent or not, are what the next phase finds in $result.
            program.Sent = sink.Results;
            program.Waiting = sink.Results.Select(record =>
                long.TryParse(record["operation"] as string, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).ToArray();
            program.WaitUntil = Environment.TickCount64 + WaitDeadlineMs;
            program.State = "waiting";
        }
        else if (program.Rule is null)
            Finish(program, "done", "");
        else
        {
            program.State = "watching";
            program.NextEvaluation = Environment.TickCount64 + RuleCooldownMs;
        }
        return sent;
    }

    private static void Finish(Program program, string state, string reason)
    {
        program.State = state;
        program.Reason = ShellHost.Printable(reason);
        program.Waiting = [];
    }
}

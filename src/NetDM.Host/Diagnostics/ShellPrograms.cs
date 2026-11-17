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
    /// <summary>
    /// What one program may hold, in formatted characters: its own <c>$state</c>, the event and results it
    /// carries and the last observation of its rule. A program over it is faulted, not trimmed: a cut
    /// state would mislead it. A device holds at most <see cref="MaxPerDevice"/> such programs.
    /// </summary>
    public const int MaxStateBytes = 4096;
    /// <summary>The budget in force: <see cref="MaxStateBytes"/> unless NetDM:ProgramStateBytes sets another (256–65536).</summary>
    public static int StateBudget { get; set; } = MaxStateBytes;
    /// <summary>Bounds of <c>every N</c>: a timer program runs at most once in this many seconds.</summary>
    public const int MinEverySeconds = 10, MaxEverySeconds = 3600;
    public const int EvaluateEveryMs = 1000, PassEveryMs = 250, WaitDeadlineMs = 30_000;
    /// <summary>
    /// A rule is evaluated again only when something it can read changed (§14.5): the world's version, the
    /// wiring report, the directory revision, the programs themselves or the owner's operations and their outcomes (its jobs). <see cref="EvaluateEveryMs"/> is the
    /// shortest spacing; <see cref="RefreshEveryMs"/> re-reads an unchanged network now and then, for what
    /// no index covers (access granted or taken back).
    /// </summary>
    public const int RefreshEveryMs = 30_000;

    private readonly record struct ChangeIndex(long World, object Wiring, long Directory, long Programs, long Operations, long Events)
    {
        public bool Same(ChangeIndex other) => World == other.World && ReferenceEquals(Wiring, other.Wiring) &&
            Directory == other.Directory && Programs == other.Programs && Operations == other.Operations &&
            Events == other.Events;
    }

    private static long _programsRevision;

    /// <summary>P10 counters: due passes, rule evaluations, rules skipped as unchanged, rules that fired, phases run.</summary>
    public static long Passes, Evaluations, Unchanged, Fired, PhasesRun;
    /// <summary>Whether the last <see cref="Tick"/> was a due pass.</summary>
    public static bool Due { get; private set; }
    public static int Count => Programs.Count;

    /// <summary>Durations of durable writes of the programs file.</summary>
    public static readonly StageTimer Writes = new();

    public static void ResetCounters()
    {
        Passes = Evaluations = Unchanged = Fired = PhasesRun = 0;
        Writes.Reset();
    }
    private static string? _programsText;
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

    /// <summary>
    /// A program is a network entity (§14.4): its ID is its identity for the round, its generation grows with
    /// every migration, its revision with every phase it completed. One copy executes: a migration fences the
    /// source in the same step that activates the target, and a device's media that still names the old copy
    /// gets "moved" instead of a second one.
    /// </summary>
    private sealed class Program
    {
        public int Id;
        public int Generation = 1;
        public long Revision;
        public string Name = "", Instance = "", State = "running", Reason = "", Resume = "running", ThrottledFrom = "running";
        public EndpointKey Owner, Provider;
        public string? Rule;
        public bool IsEventRule => Rule is { } rule && rule.StartsWith("events", StringComparison.Ordinal) &&
            (rule.Length == 6 || char.IsWhiteSpace(rule[6]) || rule[6] == '|');
        /// <summary>Last consumed event sequence in the receiver's acknowledged journal.</summary>
        public long EventCursor;
        /// <summary>The text it was started from: a device that keeps it on its media starts it again with it.</summary>
        public string? Source;
        /// <summary>Kept on the device's media: it waits for its endpoint to come back like a restored program.</summary>
        public bool Kept;
        public string[] Phases = [];
        /// <summary>Seconds between runs of a timer program ("every N"); 0 for any other.</summary>
        public int Every;
        /// <summary>Subscriptions declared by this program; keys belong only to its entity ID.</summary>
        public ProgramWatch[] Watches = [];
        /// <summary>The program's own <c>$state</c>: what its phases kept for the next phase and the next run.</summary>
        public IReadOnlyList<ShellRecord> Memory = [];
        public string? LastEvent;
        public int Phase, Runs;
        public long NextEvaluation, WaitUntil, SuspendedAt, Fuel, RefreshAt;
        /// <summary>What the rule last read; null until it has been evaluated in this runtime.</summary>
        public ChangeIndex? Seen;
        /// <summary>Sources the rule read at its last evaluation; null reads as all of them.</summary>
        public HashSet<string>? Reads;
        public long[] Waiting = [];
        public IReadOnlyList<ShellRecord> Sent = [], Event = [], Result = [];
        public bool Finished => State is "done" or "faulted" or "stopped" or "migrated";
        public Program Copy() => (Program)MemberwiseClone();
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
        List<Dictionary<string, JsonElement>> Event, string? Source = null, bool Kept = false, int Every = 0,
        List<Dictionary<string, JsonElement>>? Memory = null, int Generation = 1, long Revision = 0,
        long EventCursor = 0);

    /// <summary>Where an entity went: a device's media may still name it under its old device.</summary>
    private sealed record Moved(int Id, string From, string Name, string OwnerId, string? Source, string To, int Generation);

    private sealed record SavedFile(int Version, Guid Round, int NextId, List<SavedProgram> Programs, List<Moved>? Moves = null);

    /// <summary>Recent migrations, oldest first, at most <see cref="MaxTotal"/>.</summary>
    private static readonly List<Moved> Moves = [];

    private static (string Directory, Guid Round)? _pendingBind;
    private static readonly object BindGate = new();
    private static string? _path;
    private static Guid _round;
    private static string? _written, _writtenStructure;
    private static long _lastWrite;
    /// <summary>
    /// What restoring depends on (programs, their state, phase, runs, the "phase runs" mark) is written at
    /// once. Fuel, reasons and last observations only change what a restored program reports: they are
    /// written at most this often, so a busy pass does not wait on the disk for them.
    /// </summary>
    public const int LazyWriteMs = 5_000;

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
        Moves.Clear();
        _nextId = 0;
        _written = _writtenStructure = null;
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
            Moves.AddRange((saved.Moves ?? []).TakeLast(MaxTotal));
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
            Event = saved.Event.Select(Record).ToArray(), Source = saved.Source, Kept = saved.Kept, Every = saved.Every,
            Memory = saved.Memory?.Select(Record).ToArray() ?? [], Generation = saved.Generation, Revision = saved.Revision,
            EventCursor = saved.EventCursor, Detached = true, SuspendedAt = now,
            Watches = saved.Source is null ? [] : ProgramWatchSyntax.Parse(saved.Source).Watches.ToArray()
        };
        if (saved.InPhase)
        {
            // The Host stopped inside this phase: what it sent is unknown, and it is not sent again.
            program.Sent = [];
            program.Reason = "restored after an interrupted phase";
            if (program.Phase + 1 < program.Phases.Length)
                program.State = "waiting";
            else if (program.Rule is null && program.Every == 0)
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
        program.Seen = null;
        program.Reads = null;
        if (program.IsEventRule)
        {
            program.EventCursor = 0; // A new runtime owns a new, empty acknowledged event stream.
            program.LastEvent = null;
        }
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

    /// <summary>
    /// Writes the programs when they differ from what is on disk: temporary file, then rename. A change
    /// restoring depends on, or <paramref name="durable"/>, is written now; the rest waits for <see cref="LazyWriteMs"/>.
    /// </summary>
    private static void Save(bool durable = false)
    {
        // What "programs" shows, without fuel: a rule's own spending must not wake it again.
        var visible = string.Join(";", Programs.Select(program =>
            $"{program.Id}|{program.Instance}|{program.Name}|{program.State}|{program.Runs}|{program.Reason}"));
        if (visible != _programsText)
        {
            _programsText = visible;
            _programsRevision++; // Rules that read "programs" see the change.
        }
        if (_path is null)
            return;
        var structure = string.Join(";", Programs.Where(program => !program.Finished).Select(program =>
            $"{program.Id}|{program.Owner}|{program.Provider}|{program.State}|{program.Resume}|{program.Phase}|{program.Runs}|{program.InPhase}|{program.Kept}|{program.Source?.GetHashCode()}|{program.Generation}|{program.EventCursor}"))
            + "#" + Moves.Count.ToString(CultureInfo.InvariantCulture);
        var now = Environment.TickCount64;
        if (!durable && structure == _writtenStructure && now - _lastWrite < LazyWriteMs)
            return;
        var file = new SavedFile(FileVersion, _round, _nextId, Programs.Where(program => !program.Finished).Select(program => new SavedProgram(
            program.Id, program.Name, program.Instance, program.Owner.Id, program.Owner.Generation, program.Provider.Id, program.Provider.Generation,
            program.Rule, program.Phases, program.LastEvent, program.Phase, program.Runs, program.State, program.Resume, program.ThrottledFrom,
            program.Reason, program.Fuel, program.InPhase, Fields(program.Sent), Fields(program.Event), program.Source, program.Kept, program.Every, Fields(program.Memory), program.Generation,
            program.Revision, program.EventCursor)).ToList(), Moves.ToList());
        var text = JsonSerializer.Serialize(file);
        if (text == _written)
        {
            _writtenStructure = structure;
            return;
        }
        var writeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                // No flush to the device: the rename makes the file whole for a Host that dies, and the OS keeps
                // what was written. A machine that dies loses the round with DreamDaemon; waiting on the disk here
                // cost a busy pass 40 ms (P10 load).
                stream.Flush(false);
            }
            File.Move(temporary, _path, true);
            _written = text;
            _writtenStructure = structure;
            _lastWrite = now;
            SaveError = null;
            Writes.Record(writeStarted, true);
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

    /// <summary>Per-session lease of subscriptions owned by resident programs, never by DM or player watches.</summary>
    private sealed record WatchLease(ProgramWatch Declaration, long DirectoryRevision, long RetryAt);
    private static readonly Dictionary<(EndpointKey Owner, string Key), WatchLease> WatchLeases = [];

    /// <summary>
    /// Reconcile declared watches under each program owner's rights, without any alternate transport.
    /// The subscription is a ServiceRuntime control-plane operation; actual events still need a
    /// routed publication and a receiver-side DM acknowledgement before a rule can read them.
    /// A missing service is retried on directory changes or after a bounded delay.
    /// </summary>
    private static void ReconcileWatches(GameNetworkService game, GameNetworkService.Session session, long now)
    {
        if (game.CapturePowerState(session) is not { } power)
            return;
        var desired = new Dictionary<(EndpointKey Owner, string Key), ProgramWatch>();
        foreach (var program in Programs.Where(program => !program.Finished && program.Watches.Length > 0))
        {
            if (session.Services.ResidentCommandNode(program.Owner, program.Provider, program.Instance) is not { } origin ||
                !power.World.Physical.Nodes.ContainsKey(origin) || !power.Wiring.CanExecute(origin))
                continue; // An offline device cannot maintain active network observations.
            foreach (var declaration in program.Watches)
                desired[(program.Owner, declaration.SubscriptionKey(program.Id))] = declaration;
        }

        var tools = new NetworkInteractionTools(session.Services);
        foreach (var old in WatchLeases.Keys.Where(key => !desired.ContainsKey(key)).ToArray())
        {
            // Endpoint may already be retired; ServiceRuntime then cleaned up the subscription.
            tools.Execute(new NetworkToolActor(old.Owner),
                new NetworkToolRequest(NetworkToolKind.Unwatch, old.Key));
            WatchLeases.Remove(old);
        }

        var revision = session.Services.DirectoryRevision;
        foreach (var (id, declaration) in desired)
        {
            if (WatchLeases.TryGetValue(id, out var lease) &&
                lease.Declaration == declaration && now < lease.RetryAt &&
                (lease.DirectoryRevision == revision ||
                 session.Services.HasSubscription(id.Owner, id.Key, declaration.Instance,
                     declaration.Projection, declaration.Mode, declaration.MaxPending)))
            {
                if (lease.DirectoryRevision != revision)
                    WatchLeases[id] = lease with { DirectoryRevision = revision };
                continue; // Registry growth is not a reason to re-subscribe an intact lease.
            }
            var result = tools.Execute(new NetworkToolActor(id.Owner),
                new NetworkToolRequest(NetworkToolKind.Watch, declaration.Instance,
                    Key: id.Key, Projection: declaration.Projection,
                    Mode: declaration.Mode, MaxPending: declaration.MaxPending));
            var accepted = result.Status is ServiceStatus.Ok or ServiceStatus.Duplicate;
            WatchLeases[id] = new WatchLease(declaration, revision, now +
                (accepted ? RefreshEveryMs : RuleCooldownMs));
        }
    }

    private static void Sync(GameNetworkService.Session session)
    {
        ApplyBind();
        if (_stream == session.Packets.ReceiptStreamId)
            return;
        // A new runtime: the programs stay, their endpoints come back with the world's facts.
        _stream = session.Packets.ReceiptStreamId;
        Budgets.Clear();
        WatchLeases.Clear(); // A new stream owns no subscriptions from the old one.
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
        if (command.Command == "migrate")
            return Migrate(session, command, arguments, out applied);
        var checkOnly = command.Command == "check";
        var name = arguments.GetProperty("name").GetString() ?? "";
        var text = arguments.GetProperty("source").GetString() ?? "";
        object Refuse(string status, string error, int position = 0) => new { status, error = ShellHost.Printable(error), position };
        if (!ServiceNames.IsValid(name))
            return JsonSerializer.SerializeToElement(Refuse("invalid_argument", "a program needs a name of letters, digits, '.', '_' or '-'"));

        // A device that keeps the program on its media asks for it on every attach ("ensure"). The program of
        // the same name it already runs is taken over by its current endpoint generation and keeps its state:
        // phase, last observation, runs. Only the same endpoint may take it over, and never an older generation.
        // Compile-only requests cannot claim media ownership or mutate an existing entity,
        // even if an untrusted caller supplies ensure/entity fields explicitly.
        var ensure = !checkOnly && arguments.TryGetProperty("ensure", out var ensureFlag) &&
            (ensureFlag.ValueKind == JsonValueKind.True || (ensureFlag.ValueKind == JsonValueKind.Number && ensureFlag.TryGetInt32(out var one) && one == 1));
        var kept = ensure ? Programs.Find(program => !program.Finished && program.Name == name && program.Instance == command.Instance &&
            program.Owner.Id == command.InitiatorKey.Id && program.Provider.Id == command.ProviderKey.Id) : null;
        // The media names an entity that moved to another device: that copy is fenced, not started again.
        var entity = arguments.TryGetProperty("entity", out var entityValue) && entityValue.ValueKind == JsonValueKind.Number &&
            entityValue.TryGetInt32(out var entityId) ? entityId : 0;
        // Media that names the entity is matched by it (the device may be back with a new endpoint); media
        // that does not yet is matched by device, owner and text.
        var moved = ensure && kept is null ? Moves.LastOrDefault(move => move.Name == name && (entity != 0 ? move.Id == entity
            : move.From == command.Instance && move.OwnerId == command.InitiatorKey.Id && move.Source == text)) : null;
        if (moved is not null)
            return JsonSerializer.SerializeToElement(new { status = "moved", program = moved.Id, to = moved.To, generation = moved.Generation });
        if (kept is not null && (kept.Owner.Generation > command.InitiatorKey.Generation || kept.Provider.Generation > command.ProviderKey.Generation))
            return JsonSerializer.SerializeToElement(Refuse("invalid_argument", "a newer endpoint of this device runs the program"));
        if (kept is not null && kept.Source == text)
        {
            applied = () =>
            {
                kept.Owner = command.InitiatorKey;
                kept.Provider = command.ProviderKey;
                kept.Kept = true;
                kept.DetachedSince = 0;
            };
            return JsonSerializer.SerializeToElement(new { status = "running", program = kept.Id });
        }
        if (!checkOnly && (Programs.Count(program => !program.Finished && program != kept) >= MaxTotal ||
            Programs.Count(program => !program.Finished && program != kept && program.Instance == command.Instance) >= MaxPerDevice))
            return JsonSerializer.SerializeToElement(Refuse("limit_exceeded", $"a device holds at most {MaxPerDevice} programs"));

        // Optional observe declarations precede on events; they never act during validation.
        var declarations = ProgramWatchSyntax.Parse(text);
        if (!declarations.Ok)
            return JsonSerializer.SerializeToElement(Refuse("invalid_argument", declarations.Error!, declarations.Position));
        // "on <pipeline>" or "every <seconds>" begins the executable body; wait separates phases.
        var lines = declarations.Body.Split('\n');
        string? rule = null;
        var every = 0;
        var offset = text.Length - declarations.Body.Length;
        var first = lines[0].TrimStart();
        if (first.StartsWith("on", StringComparison.Ordinal) && (first.Length == 2 || char.IsWhiteSpace(first[2])))
        {
            rule = first[2..].Trim();
            offset = lines[0].Length + 1;
            lines = lines[1..];
        }
        else if (first.StartsWith("every", StringComparison.Ordinal) && (first.Length == 5 || char.IsWhiteSpace(first[5])))
        {
            if (!int.TryParse(first[5..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out every) ||
                every < MinEverySeconds || every > MaxEverySeconds)
                return JsonSerializer.SerializeToElement(Refuse("invalid_argument", $"every <{MinEverySeconds}-{MaxEverySeconds} seconds>"));
            offset = lines[0].Length + 1;
            lines = lines[1..];
        }
        if (declarations.Watches.Count > 0 && (rule is null || !rule.StartsWith("events", StringComparison.Ordinal) ||
            rule.Length > 6 && !char.IsWhiteSpace(rule[6]) && rule[6] != '|'))
            return JsonSerializer.SerializeToElement(Refuse("invalid_argument",
                "observe declarations require an 'on events' rule", offset));

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
        var presets = new Dictionary<string, IReadOnlyList<ShellRecord>>(StringComparer.Ordinal) { ["event"] = [], ["result"] = [], ["state"] = [] };
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
        if (checkOnly)
            return JsonSerializer.SerializeToElement(new { status = "valid" }); // No ID allocated, no mutation, no actions.
        var started = new Program
        {
            Id = ++_nextId, Name = name, Instance = command.Instance, Owner = command.InitiatorKey, Provider = command.ProviderKey,
            Rule = rule, Every = every, Source = text, Kept = ensure, Watches = declarations.Watches.ToArray(),
            Phases = phases.Select(phase => phase.Source).ToArray(),
            State = rule is null ? "running" : "watching"
        };
        applied = () =>
        {
            if (kept is not null)
                Programs.Remove(kept); // The media holds a new text: it replaces the running one.
            Programs.RemoveAll(program => program.Finished && program.Instance == command.Instance && program.Owner == command.InitiatorKey);
            if (Programs.Count == MaxTotal)
                Programs.RemoveAt(Programs.FindIndex(program => program.Finished));
            Programs.Add(started);
        };
        return JsonSerializer.SerializeToElement(new { status = "started", program = started.Id });
    }

    /// <summary>
    /// Migration of an entity (§14.4): prepare (the owner's program here, idle, a target the owner may reach and
    /// call), snapshot (its state, memory and position), fence (this copy becomes "migrated" and never runs
    /// again) and activate (the copy at the target, generation + 1) happen in one step of the one loop that runs
    /// programs, so at no moment two copies run. The owner's rights stay its own: a new device lends nothing.
    /// </summary>
    private static JsonElement Migrate(GameNetworkService.Session session, HostCommand command, JsonElement arguments, out Action? applied)
    {
        applied = null;
        object Answer(string status, string? error = null, int program = 0) =>
            error is null ? new { status, program } : new { status, program, error = ShellHost.Printable(error) };
        var id = (int)arguments.GetProperty("program").GetDouble();
        var to = arguments.GetProperty("to").GetString() ?? "";
        var found = Programs.Find(program => program.Id == id && program.Instance == command.Instance &&
            program.Owner == command.InitiatorKey && program.Provider == command.ProviderKey && !program.Finished);
        if (found is null)
            return JsonSerializer.SerializeToElement(Answer("unknown_program", program: id));
        if (!ServiceNames.IsValid(to) || to == command.Instance)
            return JsonSerializer.SerializeToElement(Answer("invalid_argument", "migrate <program> <shell instance of another device>", id));
        if (found.State is "waiting" || found.InPhase)
            return JsonSerializer.SerializeToElement(Answer("busy", "the program waits for its commands: migrate it after", id));
        // The target is a shell the owner may see and call, as it could itself; hidden and missing look the same.
        if (session.Services.DiscoverableProvider(found.Owner, to) is not { ServiceType: ShellHost.ServiceType } target ||
            session.Services.ResidentCommandNode(found.Owner, target.Provider, to) is null)
            return JsonSerializer.SerializeToElement(Answer("forbidden", "no shell the owner may use there", id));
        if (NetworkDiagnostics.Trace(session.Medium.Snapshot, command.ProviderNode, target.Node, QueryHost.ReachBudget).State !=
            NetworkDiagnostics.Reachable)
            return JsonSerializer.SerializeToElement(Answer("unreachable", "the target device is out of reach from here", id));
        if (Programs.Count(program => !program.Finished && program.Instance == to) >= MaxPerDevice)
            return JsonSerializer.SerializeToElement(Answer("limit_exceeded", $"a device holds at most {MaxPerDevice} programs", id));
        var generation = found.Generation + 1;
        applied = () =>
        {
            var moved = found.Copy();
            moved.Instance = to;
            moved.Provider = target.Provider;
            moved.Generation = generation;
            moved.Kept = false; // The target's media does not hold it; the round's file does.
            moved.Detached = false;
            moved.DetachedSince = 0;
            moved.Seen = null;
            moved.Reads = null;
            moved.NextEvaluation = 0;
            found.State = "migrated";
            found.Reason = ShellHost.Printable($"moved to {to}");
            found.Waiting = [];
            Programs.Add(moved);
            Moves.Add(new Moved(found.Id, command.Instance, found.Name, found.Owner.Id, found.Source, to, generation));
            if (Moves.Count > MaxTotal)
                Moves.RemoveAt(0);
            Save(durable: true);
        };
        return JsonSerializer.SerializeToElement(new { status = "migrated", program = id, to, generation });
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

        public bool Knows(string command) => command is "invoke" or "publish" or "watch" or "unwatch";

        public IReadOnlyList<ShellRecord>? Act(string command, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error)
            => throw new InvalidOperationException("Validation must not send commands.");
    }

    /// <summary>The asker's own programs on this device.</summary>
    internal static IReadOnlyList<ShellRecord> List(GameNetworkService.Session session, ShellHost.Scope scope)
    {
        Sync(session);
        return Programs.Where(program => program.Instance == scope.Instance && program.Owner == scope.Initiator)
            .Select(program => ShellHost.Record(("program", (long)program.Id), ("name", program.Name), ("state", program.State),
                ("runs", (long)program.Runs), ("fuel", program.Fuel), ("memory", (long)StateBytes(program)),
                ("generation", (long)program.Generation), ("revision", program.Revision), ("reason", program.Reason))).ToArray();
    }

    /// <summary>What a program holds now, counted as the formatted text of its records.</summary>
    private static int StateBytes(Program program) => ShellHost.Format(program.Memory).Length + ShellHost.Format(program.Event).Length +
        ShellHost.Format(program.Sent).Length + ShellHost.Format(program.Result).Length + (program.LastEvent?.Length ?? 0);

    /// <summary>Faults a program over its state budget; true when it did.</summary>
    private static bool OverBudget(Program program)
    {
        var bytes = StateBytes(program);
        if (bytes <= StateBudget)
            return false;
        Finish(program, "faulted", $"program state {bytes} over {StateBudget} bytes");
        return true;
    }

    /// <summary>One bounded pass over the programs: finished waits continue, due rules are evaluated.</summary>
    public static void Tick(GameNetworkService game)
    {
        var now = Environment.TickCount64;
        Due = now >= _nextPass;
        if (!Due)
            return;
        _nextPass = now + PassEveryMs;
        Passes++;
        var session = game.CaptureSession();
        Sync(session);
        // Only active event-driven programs may retain an ACKed inbox view. An endpoint
        // being retired, disconnected, or losing its program discards the journal.
        session.Services.SetEventReaders(Programs.Where(program =>
            !program.Finished && program.IsEventRule && session.Services.IsAttached(program.Owner))
            .Select(program => program.Owner));
        ReconcileWatches(game, session, now);
        if (Programs.Count == 0)
        {
            Save(); // A removed last program leaves an empty file, not a stale one.
            return;
        }
        var sent = false;
        var due = new List<(Program Program, ShellHost.Scope Scope)>();
        for (var index = Programs.Count - 1; index >= 0; index--)
        {
            var program = Programs[index];
            if (session.Services.ResidentCommandNode(program.Owner, program.Provider, program.Instance) is not { } origin)
            {
                // A program carried into a new runtime waits for its endpoints; any other leaves with them.
                if ((program.Detached || program.Kept) && !program.Finished)
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
            // Only what the rule reads counts: the world and the wiring always, the rest by source.
            bool Reads(params string[] sources) => program.Reads is null || sources.Any(program.Reads.Contains);
            var changes = new ChangeIndex(power.World.Version, power.Wiring,
                Reads("services", "route") ? session.Services.DirectoryRevision : 0, Reads("programs") ? _programsRevision : 0,
                Reads("jobs") || Reads("result") ? session.Services.OperationsRevisionOf(program.Owner) : 0,
                Reads("events") ? session.Services.EventRevisionOf(program.Owner) : 0);
            if (program.State == "watching" && program.Rule is not null && now >= program.NextEvaluation && program.Seen is { } seenIndex &&
                seenIndex.Same(changes) && now < program.RefreshAt)
            {
                Unchanged++;
                continue; // Nothing it reads has changed.
            }
            if ((program.State == "running" || (program.State == "watching" && now >= program.NextEvaluation)) && !Afford(program, now))
            {
                Throttle(program, now);
                continue;
            }
            // Detach/rebind in the same transport stream can retire a reader without
            // retiring a durable program. Its old cursor must not suppress new events.
            if (program.IsEventRule && program.EventCursor > session.Services.EventRevisionOf(program.Owner))
                program.EventCursor = 0;
            var scope = new ShellHost.Scope(program.Owner, program.Instance, origin, null,
                $"pg.{_stream:N}.{program.Id.ToString(CultureInfo.InvariantCulture)}.{program.Runs.ToString(CultureInfo.InvariantCulture)}.{program.Phase.ToString(CultureInfo.InvariantCulture)}",
                program.Owner == program.Provider ? program.Provider : null,
                InvocationHost: program.Provider);
            switch (program.State)
            {
                case "watching" when program.Every > 0 && now >= program.NextEvaluation:
                    // A timer program: its next run is due. Its $event is empty; $state holds what it kept.
                    if (program.Runs >= MaxRuns)
                    {
                        Finish(program, "faulted", "run budget spent");
                        break;
                    }
                    Fired++;
                    program.Event = [];
                    program.Result = [];
                    program.Phase = 0;
                    due.Add((program, scope));
                    break;
                case "watching" when now >= program.NextEvaluation:
                    program.NextEvaluation = now + EvaluateEveryMs;
                    program.Seen = changes;
                    program.RefreshAt = now + RefreshEveryMs;
                    Evaluations++;
                    var ruleSource = new ShellHost.Source(game, session, scope,
                        program.IsEventRule ? program.EventCursor : null,
                        program.Watches.Length > 0
                            ? program.Watches.ToDictionary(watch => watch.Alias,
                                watch => watch.SubscriptionKey(program.Id), StringComparer.Ordinal)
                            : null);
                    var seen = NetShell.Run(program.Rule!, ruleSource, ShellHost.ProgramLimits);
                    program.Reads = ruleSource.Names;
                    Charge(program, seen.FuelUsed, 0);
                    if (!seen.Ok)
                    {
                        program.Reason = ShellHost.Printable(seen.Error ?? "rule failed"); // The rule keeps watching: a network may heal.
                        break;
                    }
                    var text = ShellHost.Format(seen.Records);
                    if (program.IsEventRule)
                    {
                        // Consume even events excluded by the rule's filters. The sequence
                        // refers to an already acknowledged physical delivery, not a timer.
                        program.EventCursor = ruleSource.EventReadThrough;
                        if (program.EventCursor < ruleSource.EventLatest)
                            program.Seen = null; // More events await the next bounded pass.
                        if (seen.Records.Count == 0)
                            break;
                    }
                    else
                    {
                        var first = program.LastEvent is null;
                        if (text == program.LastEvent)
                            break;
                        program.LastEvent = text;
                        if (first)
                            break; // Ordinary change rules first establish a baseline.
                    }
                    program.Reason = "";
                    if (OverBudget(program))
                        break;
                    if (program.Runs >= MaxRuns)
                    {
                        Finish(program, "faulted", "run budget spent");
                        break;
                    }
                    Fired++;
                    program.Event = seen.Records;
                    program.Result = [];
                    program.Phase = 0;
                    due.Add((program, scope));
                    break;
                case "running":
                    due.Add((program, scope));
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
                    due.Add((program, scope with { KeyPrefix = scope.KeyPrefix + "w" }));
                    break;
            }
        }
        if (due.Count > 0)
        {
            // Every phase of this pass is marked on disk with one durable write before any of them runs: a
            // crash in between runs none of them twice (at most once), and the pass waits on the disk once.
            foreach (var (program, _) in due)
            {
                if (program.Phase == 0)
                    program.Runs++;
                program.InPhase = true;
                PhasesRun++;
            }
            Save(durable: true);
            foreach (var (program, scope) in due)
                sent |= RunPhase(game, session, program, scope);
        }
        if (sent)
            game.DrainSubmissions();
        Save();
    }

    /// <summary>Runs the current phase; returns whether it sent commands. Then the program waits, finishes or watches again.</summary>
    private static bool RunPhase(GameNetworkService game, GameNetworkService.Session session, Program program, ShellHost.Scope scope)
    {
        var presets = new Dictionary<string, IReadOnlyList<ShellRecord>>(StringComparer.Ordinal)
        {
            ["event"] = program.Event, ["result"] = program.Result, ["state"] = program.Memory
        };
        var sink = new ShellHost.Sink(session, scope);
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
        if (result.Variables?.TryGetValue("state", out var kept) == true)
            program.Memory = kept; // "$state = …" in a phase: kept for the next phase and the next run.
        program.Revision++;
        if (program.Phase + 1 < program.Phases.Length)
        {
            // "wait": the commands of this phase, sent or not, are what the next phase finds in $result.
            program.Sent = sink.Results;
            program.Waiting = sink.Results.Select(record =>
                long.TryParse(record["operation"] as string, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).ToArray();
            program.WaitUntil = Environment.TickCount64 + WaitDeadlineMs;
            program.State = "waiting";
        }
        else if (program.Rule is null && program.Every == 0)
            Finish(program, "done", "");
        else
        {
            program.State = "watching";
            program.NextEvaluation = Environment.TickCount64 + (program.Every > 0 ? program.Every * 1000L : RuleCooldownMs);
        }
        OverBudget(program);
        return sent;
    }

    private static void Finish(Program program, string state, string reason)
    {
        program.State = state;
        program.Reason = ShellHost.Printable(reason);
        program.Waiting = [];
    }
}

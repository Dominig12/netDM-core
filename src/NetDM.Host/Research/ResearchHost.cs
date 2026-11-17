using System.Globalization;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;
using NetDM.Services.Research;

namespace NetDM.Host.Research;

/// <summary>
/// Research servers in the Host (P5). One durable ledger per round, opened when the game's session
/// names its round; it outlives Host sessions and resynchronizations of the same round and is replayed
/// after a Host restart. DM reports what only it knows over the control plane: the catalog, which
/// servers physically exist, their mined income and rewards. A console reaches a server only over the
/// simulated network: such commands arrive here through <see cref="ServiceRuntime.TakeHostCommands"/>
/// once their packet reached the server's node, and the answer travels back as a result leg.
/// </summary>
public sealed class ResearchHost(GameNetworkService game, IConfiguration configuration, ILogger<ResearchHost> log)
    : BackgroundService
{
    public const string ServiceType = "research.server";
    public const string InstancePrefix = "research.";
    private const int MaxCatalogChunks = 64;
    /// <summary>This loop serves every service the Host runs for a device; diagnostics publishes from it too.</summary>
    private readonly Diagnostics.DiagnosticsHost.Watch _diagnosticsWatch = Diagnostics.DiagnosticsHost.Watch.Links();
    private readonly Diagnostics.DiagnosticsHost.Watch _directoryWatch = Diagnostics.QueryHost.DirectoryWatch();
    // P10: how long each stage of the background loop takes when it has work.
    private readonly Diagnostics.StageTimer _commandsTimer = new(), _linksTimer = new(), _directoryTimer = new(), _programsTimer = new();
    private volatile bool _resetBackground;

    /// <summary>Starts a new background measurement at the next loop pass (load runs measure phase by phase).</summary>
    public void ResetBackgroundTimers() => _resetBackground = true;
    private readonly object _gate = new();
    private readonly string _directory = Path.GetFullPath(configuration.GetValue("NetDM:ResearchDirectory", "research")!);
    private ResearchLedger? _ledger;
    private string? _openError;
    private CatalogUpload? _upload;
    private long _commands, _answered, _lateAnswers;

    private sealed class CatalogUpload(string id, int count)
    {
        public readonly string Id = id;
        public readonly int Count = count;
        public readonly List<ResearchNodeDefinition> Nodes = [];
        public readonly HashSet<string> PointTypes = new(StringComparer.Ordinal);
        public int Next;
    }

    /// <summary>Opens the round's journal; the same round keeps its ledger across Host sessions.</summary>
    public void OpenRound(Guid round)
    {
        lock (_gate)
        {
            if (_ledger is { Faulted: false } open && open.Round == round)
                return;
            var sameRound = _round == round;
            if (!sameRound)
            {
                _openError = null;
                _reported = false;
                _recoveryDelayMs = RecoveryMinMs;
            }
            _round = round;
            // Shell programs of the round live next to its journal.
            Diagnostics.ShellPrograms.Bind(_directory, round);
            // A session that names its round always gets an attempt; the timer only spaces the retries.
            OpenJournal(round, sameRound);
        }
    }

    /// <summary>Durable research storage as a readiness signal (§17.4).</summary>
    public sealed record StorageReadiness(bool Ready, string State);

    /// <summary>
    /// Damage a journal found that an interrupted append cannot explain: whole records before it were
    /// acknowledged, so the file is not shortened and research stays blocked until it is restored.
    /// </summary>
    public sealed record JournalDamage(string Reason, long Offset, long Length, int WholeRecords);

    /// <summary>
    /// <c>ready</c>: the round's journal is open and its last append reached the device. <c>faulted</c>:
    /// an append failed, writes are refused until the journal is replayed. <c>unavailable</c>: the journal
    /// cannot be opened. <c>refused</c>: the file is not this round's journal of a known format and is
    /// left untouched. <c>corrupt</c>: the journal holds damage that a torn append cannot explain, so it
    /// is left untouched too and confirmed records are not cut away. <c>closed</c>: no session has named
    /// a round yet. Takes no lock.
    /// </summary>
    public StorageReadiness Storage()
    {
        var ledger = Volatile.Read(ref _ledger);
        if (ledger is null)
            return new StorageReadiness(false, _closedState);
        return ledger.Faulted ? new StorageReadiness(false, "faulted") : new StorageReadiness(true, "ready");
    }

    private const int RecoveryMinMs = 1000, RecoveryMaxMs = 30_000, RecoveryCalmMs = 2 * RecoveryMaxMs;
    private Guid? _round;
    private volatile string _closedState = "closed";
    /// <summary>What the last open attempt found damaged, if it was damage no torn append explains.</summary>
    private volatile JournalDamage? _damage;
    private long _nextRecovery, _lastAttempt = long.MinValue / 2, _recoveries;
    private int _recoveryDelayMs = RecoveryMinMs;
    private bool _reported;

    /// <summary>
    /// Replays the round's journal and proves it writable. On failure research stays blocked: nothing is
    /// guessed from DM or a cache. Called under <see cref="_gate"/>.
    /// </summary>
    private void OpenJournal(Guid round, bool sameRound)
    {
        // Retries are spaced by real time, doubling up to RecoveryMaxMs while the storage keeps failing;
        // the spacing starts over once the storage stayed quiet for RecoveryCalmMs.
        var now = Environment.TickCount64;
        if (now - _lastAttempt > RecoveryCalmMs)
            _recoveryDelayMs = RecoveryMinMs;
        _lastAttempt = now;
        Volatile.Write(ref _nextRecovery, now + _recoveryDelayMs);
        _recoveryDelayMs = Math.Min(_recoveryDelayMs * 2, RecoveryMaxMs);

        var recovering = sameRound && (_ledger is { Faulted: true } || _openError is not null);
        _closedState = "unavailable";
        _damage = null;
        var previous = _ledger;
        Volatile.Write(ref _ledger, null); // Readers take no lock: they see no ledger while the file is replayed.
        previous?.Dispose(); // The file is opened without write sharing: the previous handle goes first.
        _upload = null;
        var path = Path.Combine(_directory, $"research-{round:N}.journal");
        try
        {
            var ledger = ResearchLedger.Open(path, round);
            // A journal that was only read proves nothing about the next append; a new one was just written.
            if (ledger.RecoveredEntries > 0 && !ledger.ConfirmWritable())
            {
                Volatile.Write(ref _ledger, ledger); // Faulted: its state stays readable, writes answer journal_failed.
                Failed(path, "The research journal cannot be written.", null);
                return;
            }
            Volatile.Write(ref _ledger, ledger);
            _openError = null;
            _reported = false;
            if (recovering)
                Interlocked.Increment(ref _recoveries);
            log.LogInformation("Research ledger of round {Round}: {Entries} records recovered, {Bytes} torn bytes cut off.",
                round, ledger.RecoveredEntries, ledger.TruncatedBytes);
        }
        catch (ResearchJournalCorruptException exception)
        {
            // Records before the damage were acknowledged: cutting the journal at the damage would lose
            // them silently. The file stays as it is and only an operator restoring it ends this state;
            // a later hello tries again in case the journal was replaced in the meantime.
            _closedState = "corrupt";
            _damage = new JournalDamage(exception.Reason, exception.Offset, exception.Length, exception.WholeRecords);
            Failed(path, exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Failed(path, exception.Message, exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not this round's journal, a newer format or a record that cannot be replayed: the file is
            // left as it is, and trying again cannot change the answer.
            _closedState = "refused";
            Failed(path, exception.Message, exception);
        }
    }

    private void Failed(string path, string error, Exception? exception)
    {
        _openError = error;
        if (_reported)
            log.LogDebug(exception, "Research ledger {Path} is still unavailable: {Error}", path, error);
        else
            log.LogError(exception, "Research ledger {Path} is unavailable: {Error}", path, error);
        _reported = true;
    }

    /// <summary>
    /// Brings durable storage back without a Host restart once the device accepts writes again: the
    /// journal is replayed from disk, which also settles the append whose outcome was unknown. DM
    /// retries what was answered <c>journal_failed</c> or <c>research_unavailable</c> under the same IDs.
    /// </summary>
    private void RecoverStorage()
    {
        if (Volatile.Read(ref _ledger) is { Faulted: false } || Environment.TickCount64 < Volatile.Read(ref _nextRecovery))
            return;
        lock (_gate)
        {
            if (_round is not { } round || _ledger is { Faulted: false } ||
                (_ledger is null && _closedState is "refused" or "corrupt"))
                return;
            OpenJournal(round, true);
        }
    }

    public object Status()
    {
        lock (_gate)
        {
            var ledger = _ledger;
            return new
            {
                directory = _directory,
                round = ledger?.Round ?? _round,
                storage = Storage().State,
                recoveries = Interlocked.Read(ref _recoveries),
                error = _openError,
                damage = _damage is { } damage ? new { damage.Reason, damage.Offset, damage.Length, damage.WholeRecords } : null,
                journal = ledger?.JournalPath,
                recoveredEntries = ledger?.RecoveredEntries,
                truncatedBytes = ledger?.TruncatedBytes,
                faulted = ledger?.Faulted,
                journalFormat = ledger?.Format,
                effects = ledger?.EffectTotals() is { } effectTotals ? new { effectTotals.Pending, effectTotals.Settled } : null,
                catalog = ledger?.CatalogHash,
                catalogInstalled = ledger?.Catalog is not null,
                background = new
                {
                    commands = _commandsTimer.Snapshot(),
                    linkWatch = new
                    {
                        watched = _diagnosticsWatch.LastWatched, publications = _diagnosticsWatch.Publications, reads = _diagnosticsWatch.Reads,
                        skipped = _diagnosticsWatch.Skipped, timing = _linksTimer.Snapshot()
                    },
                    directoryWatch = new
                    {
                        watched = _directoryWatch.LastWatched, publications = _directoryWatch.Publications, reads = _directoryWatch.Reads,
                        skipped = _directoryWatch.Skipped, timing = _directoryTimer.Snapshot()
                    },
                    programs = new
                    {
                        count = Diagnostics.ShellPrograms.Count, passes = Diagnostics.ShellPrograms.Passes,
                        evaluations = Diagnostics.ShellPrograms.Evaluations, unchanged = Diagnostics.ShellPrograms.Unchanged,
                        fired = Diagnostics.ShellPrograms.Fired, phases = Diagnostics.ShellPrograms.PhasesRun, timing = _programsTimer.Snapshot(),
                        writes = Diagnostics.ShellPrograms.Writes.Snapshot()
                    }
                },
                commands = Interlocked.Read(ref _commands),
                answered = Interlocked.Read(ref _answered),
                lateAnswers = Interlocked.Read(ref _lateAnswers),
                storages = ledger?.Storages().Select(StorageDto).ToArray(),
                totals = ledger?.Totals() is { } totals ? new
                {
                    totals.Balanced,
                    minted = Whole(totals.Minted), available = Whole(totals.Available), spent = Whole(totals.Spent), lost = Whole(totals.Lost),
                    escrow = Whole(totals.Escrow)
                } : null,
                transfers = ledger?.Transfers().TakeLast(32).Select(transfer => new
                {
                    transfer.Transfer, transfer.Source, transfer.Destination, state = ServiceNames.Code(transfer.State), points = Whole(transfer.PointsMilli)
                }).ToArray()
            };
        }
    }

    // ---- DM control plane ------------------------------------------------------------------

    /// <summary>Handles a research_* request of the owning DM session. Returns (ok, error, result).</summary>
    public (bool Ok, string? Error, object? Result) Handle(string op, JsonElement? payload)
    {
        var ledger = Volatile.Read(ref _ledger);
        if (ledger is null)
            return (false, "research_unavailable", new { error = _openError });
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return (false, "invalid_request", null);
        try
        {
            return op switch
            {
                "research_catalog" => Catalog(ledger, body),
                "research_storage" => Storage(ledger, body),
                "research_income" => Income(ledger, body),
                "research_grant" => Reply(ledger.Grant(Text(body, "storage"), Integer(body, "generation"), Text(body, "source"),
                    Text(body, "kind"), MilliPoints(body.GetProperty("points")))),
                "research_boost" => Reply(ledger.Boost(Text(body, "storage"), Integer(body, "generation"), Text(body, "node"),
                    Text(body, "evidence"), WholePoints(body.GetProperty("discount")))),
                "research_debit" => Reply(ledger.Debit(Text(body, "storage"), Integer(body, "generation"), Text(body, "source"),
                    Text(body, "kind"), MilliPoints(body.GetProperty("points")))),
                "research_import" => Import(ledger, body),
                "research_replicate" => Replicate(ledger, body),
                "research_effects" => Effects(ledger, body),
                "research_knowledge" => Knowledge(ledger, body),
                "research_state" => (true, null, body.TryGetProperty("storage", out var storage) && storage.GetString() is { } id
                    ? ledger.Storage(id) is { } view ? StorageDto(view) : null : Status()),
                _ => (false, "unknown_op", null)
            };
        }
        catch (ObjectDisposedException)
        {
            return (false, "research_unavailable", null); // A new round replaced the ledger meanwhile.
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or
                                              OverflowException or ArgumentException)
        {
            return (false, "invalid_request", new { error = exception.Message });
        }
    }

    /// <summary>
    /// Chunked catalog upload: <c>{catalog, chunk, chunks, pointTypes, nodes}</c>. The last chunk
    /// validates the whole content and binds it to the round, or reports the first defect.
    /// </summary>
    private (bool, string?, object?) Catalog(ResearchLedger ledger, JsonElement body)
    {
        var id = Text(body, "catalog");
        var chunk = (int)Integer(body, "chunk");
        var chunks = (int)Integer(body, "chunks");
        if (chunks is < 1 or > MaxCatalogChunks || chunk < 0 || chunk >= chunks)
            return (false, "invalid_request", null);
        lock (_gate)
        {
            if (chunk == 0)
                _upload = new CatalogUpload(id, chunks);
            if (_upload is null || _upload.Id != id || _upload.Count != chunks || _upload.Next != chunk)
                return (false, "catalog_out_of_order", new { next = _upload?.Id == id ? _upload.Next : 0 });
            foreach (var type in body.GetProperty("pointTypes").EnumerateArray())
                _upload.PointTypes.Add(type.GetString() ?? "");
            foreach (var node in body.GetProperty("nodes").EnumerateArray())
                _upload.Nodes.Add(new ResearchNodeDefinition(Text(node, "id"), Strings(node, "prereqs"), Strings(node, "designs"),
                    WholePoints(node.GetProperty("costs")), Flag(node, "starting"), Flag(node, "hidden"), Flag(node, "experimental")));
            _upload.Next++;
            if (_upload.Next < chunks)
                return (true, null, new { next = _upload.Next });
            var upload = _upload;
            _upload = null;
            var catalog = ResearchCatalog.Create(upload.Nodes, upload.PointTypes);
            var status = ledger.InstallCatalog(catalog);
            var ok = status is ResearchStatus.Ok or ResearchStatus.Duplicate;
            return (ok, ok ? null : Code(status), new { status = Code(status), hash = catalog.Hash, nodes = catalog.Nodes.Count,
                recorded = ledger.CatalogHash });
        }
    }

    private (bool, string?, object?) Storage(ResearchLedger ledger, JsonElement body)
    {
        var id = Text(body, "storage");
        var generation = Integer(body, "generation");
        var (status, view) = Text(body, "action") switch
        {
            "register" => ledger.RegisterStorage(id, generation, Text(body, "domain")),
            "destroy" => ledger.DestroyStorage(id, generation),
            _ => (ResearchStatus.InvalidRequest, null)
        };
        var ok = status is ResearchStatus.Ok or ResearchStatus.Duplicate;
        return (ok, ok ? null : Code(status), new { status = Code(status), storage = view is null ? null : StorageDto(view) });
    }

    private static (bool, string?, object?) Import(ResearchLedger ledger, JsonElement body)
    {
        var (status, added) = ledger.Import(Text(body, "storage"), Integer(body, "generation"), Text(body, "source"), Strings(body, "nodes"));
        var ok = status is ResearchStatus.Ok or ResearchStatus.Duplicate;
        return (ok, ok ? null : Code(status), new { status = Code(status), added });
    }

    /// <summary><c>{interval, grants: [{storage, generation, points: {type: "milli"}}]}</c>, one durable flush.</summary>
    private static (bool, string?, object?) Income(ResearchLedger ledger, JsonElement body)
    {
        var grants = body.GetProperty("grants").EnumerateArray().Select(grant => new ResearchLedger.IncomeGrant(
            Text(grant, "storage"), Integer(grant, "generation"), MilliPoints(grant.GetProperty("points")))).ToArray();
        if (grants.Length is < 1 or > ResearchLedger.MaxStorages)
            return (false, "invalid_request", null);
        var statuses = ledger.GrantIncome(Integer(body, "interval"), grants);
        return (true, null, new { statuses = statuses.Select(Code).ToArray() });
    }

    /// <summary>
    /// The outbox of purchases (§13.5): <c>{settle?: [{id, result}], max?}</c>. DM first reports the effects
    /// it settled, then receives the oldest ones still pending. An effect is delivered again until its
    /// settlement is recorded, also by the next Host process, so DM applies it by ID exactly once.
    /// The world effects of a purchase belong to the server, not to the console that asked: they do not
    /// wait for the answer's way back over the game network.
    /// </summary>
    private static (bool, string?, object?) Effects(ResearchLedger ledger, JsonElement body)
    {
        var settled = 0;
        if (body.TryGetProperty("settle", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            var results = list.EnumerateArray().Select(item => (Text(item, "id"), Text(item, "result"))).ToArray();
            var (status, count) = ledger.SettleEffects(results);
            if (status is not (ResearchStatus.Ok or ResearchStatus.Duplicate))
                return (false, Code(status), new { status = Code(status) });
            settled = count;
        }
        var max = body.TryGetProperty("max", out var limit) ? (int)Math.Clamp(Number(limit, "max"), 0, MaxEffectsPerPage) : MaxEffectsPerPage;
        var effects = ledger.PendingEffects(max);
        return (true, null, new
        {
            settled,
            pending = ledger.EffectTotals().Pending,
            effects = effects.Select(effect => new
            {
                effect.Id, effect.Kind, effect.Storage, op = effect.Operation, effect.Node, price = effect.Price, effect.Revision
            }).ToArray()
        });
    }

    private const int MaxEffectsPerPage = 16;

    /// <summary>
    /// A server's own knowledge log for the world it stands in: <c>{storage, since, max?}</c> answers
    /// <c>{storage, revision, upto, more, nodes}</c>, the nodes recorded after <c>since</c> in record order.
    /// DM projects them onto the legacy web that the server's machines read, whatever brought them to the
    /// server: a purchase, a restored disk or a peer. It is the server's state, not a message between
    /// devices, so it does not wait for an answer's way over the game network; a lost answer loses nothing.
    /// Reading changes nothing, and a node projected twice is projected once.
    /// </summary>
    private static (bool, string?, object?) Knowledge(ResearchLedger ledger, JsonElement body)
    {
        var storage = Text(body, "storage");
        var since = Integer(body, "since");
        if (since < 0)
            return (false, "invalid_request", null);
        var max = body.TryGetProperty("max", out var limit) ? (int)Math.Clamp(Number(limit, "max"), 1, MaxKnowledgePerPage) : MaxKnowledgePerPage;
        var delta = ledger.Delta(storage, since, max);
        return delta is null
            ? (false, "storage_unavailable", null) // Unknown or destroyed: its knowledge left the network with it.
            : (true, null, new { storage, delta.Revision, upto = delta.Upto, more = delta.More, nodes = delta.Nodes });
    }

    private const int MaxKnowledgePerPage = 64;

    // ---- Network commands ------------------------------------------------------------------

    /// <summary>How long a reserved transfer waits for its authorization to reach the destination.</summary>
    public static readonly TimeSpan TransferTtl = TimeSpan.FromSeconds(60);
    private const int SyncBatchRecords = 50;
    private long _nextExpiry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (_resetBackground)
                {
                    _resetBackground = false;
                    foreach (var stage in new[] { _commandsTimer, _linksTimer, _directoryTimer, _programsTimer })
                        stage.Reset();
                    Diagnostics.ShellPrograms.ResetCounters();
                }
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                _commandsTimer.Record(started, AnswerCommands() > 0);
                ExpireTransfers();
                RecoverStorage();
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                _diagnosticsWatch.Publish(game);
                _linksTimer.Record(started, _diagnosticsWatch.Due && _diagnosticsWatch.LastWatched > 0);
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                _directoryWatch.Publish(game);
                _directoryTimer.Record(started, _directoryWatch.Due && _directoryWatch.LastWatched > 0);
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                Diagnostics.ShellPrograms.Tick(game);
                Diagnostics.NetworkSpace.Tick(game);
                _programsTimer.Record(started, Diagnostics.ShellPrograms.Due && Diagnostics.ShellPrograms.Count > 0);
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Research command handling failed.");
            }
        }
    }

    /// <summary>Answers the commands that reached research servers; the ledger decides, the network carries the answer.</summary>
    public int AnswerCommands()
    {
        var session = game.CaptureSession();
        var services = session.Services;
        var commands = services.TakeHostCommands(64);
        if (commands.Length == 0)
            return 0;
        foreach (var command in commands)
        {
            Action? applied = null;
            Interlocked.Increment(ref _commands);
            // One queue serves every service the Host runs for a device; each type answers its own.
            var result = command.ServiceType switch
            {
                Diagnostics.DiagnosticsHost.ServiceType => Diagnostics.DiagnosticsHost.Answer(game, command),
                Diagnostics.QueryHost.ServiceType => Diagnostics.QueryHost.Answer(session, command),
                Diagnostics.ShellHost.ServiceType => Diagnostics.ShellHost.Answer(game, session, command, out applied),
                _ => Answer(command)
            };
            var (status, _) = services.Acknowledge(command.DeliveryId, true, null, result);
            if (status == ServiceStatus.Ok)
            {
                applied?.Invoke();
                Interlocked.Increment(ref _answered);
            }
            else
                Interlocked.Increment(ref _lateAnswers); // The session changed or the delivery expired; a retry gets the recorded answer.
        }
        game.DrainSubmissions();
        return commands.Length;
    }

    /// <summary>Settles transfers whose authorization did not arrive in time: a living source gets its escrow back.</summary>
    private void ExpireTransfers()
    {
        var now = Environment.TickCount64;
        if (now < _nextExpiry)
            return;
        _nextExpiry = now + 1000;
        try
        {
            var expired = Volatile.Read(ref _ledger)?.ExpireTransfers(NowMs()) ?? 0;
            if (expired > 0)
                log.LogInformation("Research transfers expired: {Count}.", expired);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private JsonElement Answer(HostCommand command)
    {
        var ledger = Volatile.Read(ref _ledger);
        var storage = command.Instance.StartsWith(InstancePrefix, StringComparison.Ordinal)
            ? command.Instance[InstancePrefix.Length..] : "";
        using var arguments = JsonDocument.Parse(command.Arguments);
        var args = arguments.RootElement;
        try
        {
            switch (command.Command)
            {
                case "sync":
                    return Sync(ledger, storage, args.GetProperty("since").GetInt64());
                case "transfer_out":
                    return TransferOut(ledger, storage, args);
                case "transfer_in":
                    return TransferIn(ledger, storage, args);
                case "backup" or "restore":
                    return DiskCommand(ledger, storage, command.Command, args.GetProperty("disk").GetString() ?? "");
            }
        }
        catch (ObjectDisposedException)
        {
            ledger = null; // A new round replaced the ledger: the answers below report the server as unavailable.
        }
        if (command.Command is "sync" or "transfer_out" or "transfer_in" or "backup" or "restore")
            return Unavailable(command.Command, args);
        if (command.Command == "status")
        {
            var view = ledger?.Storage(storage);
            var catalog = ledger?.Catalog;
            var state = view is null || view.Destroyed ? "destroyed" : catalog is null ? "catalog_unavailable" : "online";
            // The console sees the network's account: its server's points and those of the servers it reaches.
            var pooled = view is null || catalog is null || ledger is null ? 0
                : ledger.PooledMilli(storage, Payers(ledger, storage, view.Domain), catalog.PrimaryPointType) / ResearchLedger.Milli;
            return Json(new
            {
                state, revision = view?.Revision ?? 0, available = pooled,
                unlocked = view?.Unlocked.Count ?? 0, catalog = ledger?.CatalogHash?[..12] ?? ""
            });
        }
        var node = args.GetProperty("node").GetString() ?? "";
        var revision = args.TryGetProperty("revision", out var expected) ? expected.GetInt64() : (long?)null;
        ResearchCommandResult result;
        try
        {
            result = ledger is null
                ? new ResearchCommandResult(ResearchOutcome.StorageUnavailable, node, 0, 0, 0)
                : ledger.Research(storage, command.IdempotencyKey, node, args.GetProperty("quote").GetInt64(), revision,
                    Payers(ledger, storage, ledger.Storage(storage)?.Domain)).Result;
        }
        catch (ObjectDisposedException)
        {
            result = new ResearchCommandResult(ResearchOutcome.StorageUnavailable, node, 0, 0, 0);
        }
        return Json(new
        {
            outcome = ServiceNames.Code(result.Outcome), node = result.Node, revision = result.Revision,
            price = Math.Max(0, result.Price), available = Math.Max(0, result.Available)
        });
    }

    /// <summary>
    /// The servers that share their points with this one: the living storages of its domain whose servers
    /// it reaches over the game network now, nearest first. A server cut off by the network, or with its
    /// node gone, pays nothing until it is reachable again.
    /// </summary>
    private List<string> Payers(ResearchLedger ledger, string storage, string? domain)
    {
        var payers = new List<string>();
        var services = game.Services;
        if (domain is null || services.ProviderNode(InstancePrefix + storage) is not { } source)
            return payers;
        var nodes = new Dictionary<NodeId, string>();
        foreach (var peer in ledger.Storages())
            if (peer.StorageId != storage && !peer.Destroyed && peer.Domain == domain &&
                services.ProviderNode(InstancePrefix + peer.StorageId) is { } node && node != source)
                nodes.TryAdd(node, peer.StorageId);
        if (nodes.Count == 0)
            return payers;
        var reached = SharedMediumNetwork.ReachAll(game.Snapshot, [source], nodes.Keys.ToHashSet());
        payers.AddRange(reached.OrderBy(entry => entry.Value.Cost).ThenBy(entry => nodes[entry.Key], StringComparer.Ordinal)
            .Select(entry => nodes[entry.Key]));
        return payers;
    }

    /// <summary>The provider's knowledge log after the peer's watermark, in one bounded batch.</summary>
    private static JsonElement Sync(ResearchLedger? ledger, string storage, long since)
    {
        var catalog = ledger?.Catalog;
        var delta = ledger?.Delta(storage, since, SyncBatchRecords);
        if (delta is null || catalog is null)
            return Json(new { nodes = "", boosts = "", upto = since, more = false, revision = 0L });
        var boosts = string.Join(";", delta.Boosts.Select(pair => $"{pair.Key}={pair.Value.GetValueOrDefault(catalog.PrimaryPointType)}"));
        return Json(new { nodes = string.Join(",", delta.Nodes), boosts, upto = delta.Upto, more = delta.More, revision = delta.Revision });
    }

    /// <summary>Reserve on the source (this server) toward the requested destination.</summary>
    private static JsonElement TransferOut(ResearchLedger? ledger, string source, JsonElement args)
    {
        var transfer = args.GetProperty("transfer").GetString() ?? "";
        var amount = args.GetProperty("amount").GetInt64();
        var catalog = ledger?.Catalog;
        var view = ledger?.Storage(source);
        if (ledger is null || catalog is null || view is null)
            return TransferAnswer(transfer, "refused", "storage_unavailable", amount, 0);
        var (status, record) = ledger.Reserve(transfer, source, view.Generation, args.GetProperty("to").GetString() ?? "",
            new Dictionary<string, long> { [catalog.PrimaryPointType] = amount * ResearchLedger.Milli },
            NowMs() + (long)TransferTtl.TotalMilliseconds);
        var available = (ledger.Storage(source)?.AvailableMilli.GetValueOrDefault(catalog.PrimaryPointType) ?? 0) / ResearchLedger.Milli;
        return record is not null && status is ResearchStatus.Ok or ResearchStatus.Duplicate
            ? TransferAnswer(transfer, ServiceNames.Code(record!.State), null, amount, available)
            : TransferAnswer(transfer, "refused", Code(status), amount, available);
    }

    /// <summary>The authorization reached the destination (this server): commit, once.</summary>
    private static JsonElement TransferIn(ResearchLedger? ledger, string destination, JsonElement args)
    {
        var transfer = args.GetProperty("transfer").GetString() ?? "";
        var amount = args.GetProperty("amount").GetInt64();
        var catalog = ledger?.Catalog;
        var view = ledger?.Storage(destination);
        var record = ledger?.Transfer(transfer);
        if (ledger is null || catalog is null || view is null || record is null)
            return TransferAnswer(transfer, "refused", record is null ? "unknown_transfer" : "storage_unavailable", amount, 0);
        // The authorization names its source and amount; anything else is not this transfer.
        if (record.Source != args.GetProperty("from").GetString() ||
            record.PointsMilli.GetValueOrDefault(catalog.PrimaryPointType) != amount * ResearchLedger.Milli)
            return TransferAnswer(transfer, "refused", "mismatch", amount, 0);
        var (status, committed) = ledger.CommitTransfer(transfer, destination, view.Generation, NowMs());
        var available = (ledger.Storage(destination)?.AvailableMilli.GetValueOrDefault(catalog.PrimaryPointType) ?? 0) / ResearchLedger.Milli;
        return status is ResearchStatus.Ok or ResearchStatus.Duplicate or ResearchStatus.TransferClosed
            ? TransferAnswer(transfer, ServiceNames.Code(committed!.State), status == ResearchStatus.TransferClosed ? "closed" : null, amount, available)
            : TransferAnswer(transfer, "refused", Code(status), amount, available);
    }

    private static JsonElement DiskCommand(ResearchLedger? ledger, string storage, string command, string disk)
    {
        var view = ledger?.Storage(storage);
        if (ledger is null || view is null)
            return Json(new { disk, state = "refused", nodes = 0, reason = "storage_unavailable" });
        if (command == "backup")
        {
            var (status, written) = ledger.Backup(disk, storage, view.Generation);
            return status switch
            {
                ResearchStatus.Ok => Json(new { disk, state = "written", nodes = written!.Nodes }),
                ResearchStatus.Duplicate => Json(new { disk, state = "unchanged", nodes = written!.Nodes }),
                _ => Json(new { disk, state = "refused", nodes = 0, reason = Code(status) })
            };
        }
        var (restoreStatus, added) = ledger.Restore(disk, storage, view.Generation);
        return restoreStatus switch
        {
            ResearchStatus.Ok => Json(new { disk, state = "restored", nodes = added }),
            ResearchStatus.Duplicate => Json(new { disk, state = "unchanged", nodes = 0 }),
            _ => Json(new { disk, state = "refused", nodes = 0, reason = Code(restoreStatus) })
        };
    }

    private static JsonElement Unavailable(string command, JsonElement args) => command switch
    {
        "sync" => Json(new { nodes = "", boosts = "", upto = args.GetProperty("since").GetInt64(), more = false, revision = 0L }),
        "backup" or "restore" => Json(new { disk = args.GetProperty("disk").GetString(), state = "refused", nodes = 0, reason = "storage_unavailable" }),
        _ => TransferAnswer(args.GetProperty("transfer").GetString() ?? "", "refused", "storage_unavailable", args.GetProperty("amount").GetInt64(), 0)
    };

    private static JsonElement TransferAnswer(string transfer, string state, string? reason, long amount, long available) =>
        reason is null
            ? Json(new { transfer, state, amount, available = Math.Max(0, available) })
            : Json(new { transfer, state, reason, amount, available = Math.Max(0, available) });

    /// <summary><c>{storage, generation, peer, upto, nodes, boosts: {node: whole}}</c>: knowledge a server received over the network.</summary>
    private static (bool, string?, object?) Replicate(ResearchLedger ledger, JsonElement body)
    {
        var catalog = ledger.Catalog;
        if (catalog is null)
            return (false, "catalog_unavailable", null);
        var boosts = body.TryGetProperty("boosts", out var list) && list.ValueKind == JsonValueKind.Object
            ? list.EnumerateObject().ToDictionary(pair => pair.Name,
                pair => (IReadOnlyDictionary<string, long>)new Dictionary<string, long> { [catalog.PrimaryPointType] = Number(pair.Value, pair.Name) },
                StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
        var (status, added) = ledger.Replicate(Text(body, "storage"), Integer(body, "generation"), Text(body, "peer"), Integer(body, "upto"),
            Strings(body, "nodes"), boosts);
        var ok = status is ResearchStatus.Ok or ResearchStatus.Duplicate;
        return (ok, ok ? null : Code(status), new { status = Code(status), added, watermark = ledger.Watermark(Text(body, "storage"), Text(body, "peer")) });
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static (bool, string?, object?) Reply(ResearchStatus status) =>
        (status is ResearchStatus.Ok or ResearchStatus.Duplicate, status is ResearchStatus.Ok or ResearchStatus.Duplicate ? null : Code(status),
            new { status = Code(status) });

    private static object StorageDto(ResearchStorageView view) => new
    {
        storage = view.StorageId, view.Generation, view.Domain, view.Destroyed, view.Revision,
        unlocked = view.Unlocked.Count, view.Knowledge, available = Whole(view.AvailableMilli), availableMilli = view.AvailableMilli
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(CultureInfo.InvariantCulture)),
        incomeIntervals = view.IncomeIntervals
    };

    private static Dictionary<string, long> Whole(IReadOnlyDictionary<string, long> milli) =>
        milli.ToDictionary(pair => pair.Key, pair => pair.Value / ResearchLedger.Milli);

    private static string Code(ResearchStatus status) => ServiceNames.Code(status);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new FormatException($"'{name}' must be text.");

    /// <summary>An integer from a JSON number or a decimal string; DM numbers are 32-bit floats.</summary>
    private static long Integer(JsonElement element, string name) => Number(element.GetProperty(name), name);

    private static long Number(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.Number when value.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < 1e15 => (long)real,
        JsonValueKind.String => long.Parse(value.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"'{name}' must be an integer.")
    };

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => false
        };

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];

    private static Dictionary<string, long> WholePoints(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(pair => pair.Name, pair => Number(pair.Value, pair.Name), StringComparer.Ordinal)
            : new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Milli-points as decimal strings or integer numbers.</summary>
    private static Dictionary<string, long> MilliPoints(JsonElement element) => WholePoints(element);

    public override void Dispose()
    {
        lock (_gate)
        {
            _ledger?.Dispose();
            _ledger = null;
        }
        base.Dispose();
    }
}

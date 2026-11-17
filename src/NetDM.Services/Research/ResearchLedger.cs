using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetDM.Services.Research;

public enum ResearchStatus : byte
{
    Ok, Duplicate, InvalidRequest, UnknownStorage, StorageDestroyed, Conflict, UnknownNode,
    CatalogUnavailable, CatalogMismatch, LimitExceeded, JournalFailed,
    Forbidden, InsufficientPoints, UnknownTransfer, TransferClosed, UnknownDisk
}

/// <summary>A point transfer (§13.7): reserved into escrow, then committed to the destination or settled.</summary>
public enum TransferState : byte { Reserved, Committed, Cancelled, Expired, Lost }

public sealed record TransferView(string Transfer, string Source, string Destination, IReadOnlyDictionary<string, long> PointsMilli,
    TransferState State, long DeadlineMs);

/// <summary>A backup disk's manifest: knowledge only, never a balance (§13.8).</summary>
public sealed record DiskView(string Disk, string Source, long Version, int Nodes, string CatalogHash);

/// <summary>
/// Knowledge a storage recorded after <c>since</c>, in record order: unlocked nodes and raised boosts.
/// <c>Upto</c> is the last record included; with <c>More</c> the next batch starts there.
/// </summary>
public sealed record KnowledgeDelta(string Storage, long Revision, long Upto, bool More, IReadOnlyList<string> Nodes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Boosts);

/// <summary>Outcome of a purchase; every one of them is a valid answer to the console.</summary>
public enum ResearchOutcome : byte
{
    Researched, AlreadyResearched, PrerequisitesMissing, QuoteChanged, InsufficientPoints,
    UnknownNode, StaleRevision, StorageUnavailable, CatalogUnavailable
}

/// <summary>A purchase answer. Points are milli-points of the catalog's primary point type.</summary>
public sealed record ResearchCommandResult(ResearchOutcome Outcome, string Node, long Revision, long PriceMilli, long AvailableMilli)
{
    public long Price => PriceMilli / ResearchLedger.Milli;
    public long Available => AvailableMilli / ResearchLedger.Milli;
}

/// <summary><c>Knowledge</c> is the last record of the storage's knowledge log, see <see cref="KnowledgeDelta"/>.</summary>
public sealed record ResearchStorageView(string StorageId, long Generation, string Domain, bool Destroyed, long Revision,
    IReadOnlyList<string> Unlocked, IReadOnlyDictionary<string, long> AvailableMilli, int IncomeIntervals, long Knowledge);

/// <summary>
/// What a committed purchase owes the world (§13.5): the unlock for the machines that read it, the
/// science bounty and the research log. The obligation exists from the purchase's own journal record
/// and stays pending, across Host restarts, until DM reports how it settled it. Its ID is stable, so
/// DM applies it once however often it is delivered.
/// </summary>
public sealed record ResearchEffect(string Id, string Kind, string Storage, string Operation, string Node, long PriceMilli, long Revision)
{
    public const string Researched = "researched";
    public long Price => PriceMilli / ResearchLedger.Milli;
}

/// <summary>Pending obligations and how the settled ones ended, by the world's result code.</summary>
public sealed record ResearchEffectTotals(int Pending, IReadOnlyDictionary<string, int> Settled);

/// <summary>Per point type: everything minted equals what is available, held in escrow, spent and lost.</summary>
public sealed record ResearchTotals(IReadOnlyDictionary<string, long> Minted, IReadOnlyDictionary<string, long> Available,
    IReadOnlyDictionary<string, long> Spent, IReadOnlyDictionary<string, long> Lost, IReadOnlyDictionary<string, long> Escrow)
{
    public bool Balanced => Minted.Keys.Concat(Available.Keys).Concat(Spent.Keys).Concat(Lost.Keys).Concat(Escrow.Keys).Distinct()
        .All(type => Get(Minted, type) == Get(Available, type) + Get(Escrow, type) + Get(Spent, type) + Get(Lost, type));

    private static long Get(IReadOnlyDictionary<string, long> values, string type) => values.GetValueOrDefault(type);
}

/// <summary>
/// The research ledger of one round: one storage per physical research server, each with its own
/// knowledge and its own wallet. It is the single writer of research state in the Host.
///
/// Every change is first appended to the durable journal as the effect it has, then applied in memory.
/// Replay therefore needs no catalog and repeats exactly what was acknowledged. A purchase is decided
/// against the installed catalog; its operation ID returns the recorded answer forever after, also
/// after a Host restart, so a repeated request never spends twice. Income is keyed by its interval
/// and a reward by its source event, so neither is paid twice.
/// </summary>
public sealed class ResearchLedger : IDisposable
{
    public const long Milli = 1000;
    public const int MaxStorages = 256;
    public const int MaxOperations = 100_000;
    public const long MaxGrantMilli = 1_000_000_000_000;
    public const int MaxTransfers = 10_000;
    public const int MaxDisks = 4_096;
    public const int MaxEffectsPerSettle = 64;
    /// <summary>
    /// Version of what the journal's records mean. A journal names its version in a <c>format</c> record;
    /// a build refuses a journal of a newer version instead of misreading it, so a rolled-back Host never
    /// continues a round on records it does not understand. Version 2: a purchase record carries its
    /// world effect, settled by an <c>effect</c> record. Purchases recorded before the format record of
    /// an upgraded journal owe nothing: their world effects were applied by the older protocol.
    /// </summary>
    public const int JournalFormat = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private readonly object _gate = new();
    private readonly ResearchJournal _journal;
    private readonly Dictionary<string, StorageState> _storages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResearchCommandResult> _operations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _redemptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _minted = new(StringComparer.Ordinal), _spent = new(StringComparer.Ordinal),
        _lost = new(StringComparer.Ordinal), _escrow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferRecord> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiskRecord> _disks = new(StringComparer.Ordinal);
    /// <summary>Pending world effects in the order their purchases were recorded.</summary>
    private readonly SortedDictionary<long, ResearchEffect> _pendingEffects = new();
    private readonly Dictionary<string, long> _effectOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _settledEffects = new(StringComparer.Ordinal);
    private long _effectSeq;
    private int _format = 1;
    private ResearchCatalog? _catalog;
    private string? _catalogHash;
    private volatile bool _faulted;

    private sealed class StorageState
    {
        public string Id = "", Domain = "";
        public long Generation, Revision;
        public bool Destroyed;
        public readonly HashSet<string> Unlocked = new(StringComparer.Ordinal);
        public readonly HashSet<string> Revealed = new(StringComparer.Ordinal);
        public readonly HashSet<string> Evidence = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, long>> Boosts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Available = new(StringComparer.Ordinal);
        public readonly HashSet<long> Intervals = new();
        /// <summary>Knowledge log: every unlock and every raised boost takes the next record number.</summary>
        public long KnowledgeSeq;
        public readonly Dictionary<string, long> NodeSeq = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> BoostSeq = new(StringComparer.Ordinal);
        /// <summary>Peer storage -> last record of its log already replicated here.</summary>
        public readonly Dictionary<string, long> Peers = new(StringComparer.Ordinal);

        public bool Unlock(string node)
        {
            if (!Unlocked.Add(node))
                return false;
            NodeSeq[node] = ++KnowledgeSeq;
            return true;
        }

        /// <summary>Boosts merge by the best discount per point type, never by summing.</summary>
        public bool MergeBoost(string node, IReadOnlyDictionary<string, long>? discount)
        {
            Revealed.Add(node);
            if (!Boosts.TryGetValue(node, out var current))
                Boosts[node] = current = new Dictionary<string, long>(StringComparer.Ordinal);
            var raised = false;
            foreach (var (type, amount) in discount ?? new Dictionary<string, long>())
                if (amount > current.GetValueOrDefault(type))
                {
                    current[type] = amount;
                    raised = true;
                }
            if (raised)
                BoostSeq[node] = ++KnowledgeSeq;
            return raised;
        }

        /// <summary>Whether merging this discount would raise the stored one.</summary>
        public bool Raises(string node, IReadOnlyDictionary<string, long> discount) =>
            discount.Any(pair => pair.Value > (Boosts.TryGetValue(node, out var current) ? current.GetValueOrDefault(pair.Key) : 0));
    }

    private sealed class TransferRecord
    {
        public string Id = "", Source = "", Destination = "";
        public long SourceGeneration, DestinationGeneration, Deadline;
        public Dictionary<string, long> Points = new(StringComparer.Ordinal);
        public TransferState State;
    }

    private sealed class DiskRecord
    {
        public string Id = "", Source = "", Hash = "";
        public long Version;
        public string[] Nodes = [];
        public Dictionary<string, Dictionary<string, long>> Boosts = new(StringComparer.Ordinal);
    }

    /// <summary>One journal record: the effect of an accepted change.</summary>
    private sealed record Entry(string K, Guid? Round = null, string? Hash = null, string? Storage = null,
        long Generation = 0, string? Domain = null, string[]? Nodes = null, long Interval = 0, string? Source = null,
        string? Kind = null, string? Node = null, string? Evidence = null, Dictionary<string, long>? Points = null,
        string? Op = null, string? Outcome = null, long Revision = 0, long Available = 0, long Price = 0,
        string? Peer = null, long Upto = 0, Dictionary<string, Dictionary<string, long>>? Boosts = null, string? Transfer = null,
        string? Destination = null, long DestinationGeneration = 0, long Deadline = 0, string? Disk = null, long Version = 0,
        string? State = null, Dictionary<string, Dictionary<string, long>>? Payers = null,
        Dictionary<string, string>? Effects = null);

    private ResearchLedger(ResearchJournal journal, Guid round)
    {
        _journal = journal;
        Round = round;
    }

    public Guid Round { get; }
    public string JournalPath => _journal.Path;
    /// <summary>The journal's content version; always <see cref="JournalFormat"/> once the ledger is open.</summary>
    public int Format { get { lock (_gate) return _format; } }
    public int RecoveredEntries { get; private set; }
    public long TruncatedBytes => _journal.TruncatedBytes;
    public string? CatalogHash { get { lock (_gate) return _catalogHash; } }
    public ResearchCatalog? Catalog { get { lock (_gate) return _catalog; } }
    /// <summary>
    /// An append failed: its outcome on disk is unknown, so nothing more is written or applied here.
    /// Only reopening the journal, which replays what the disk really holds, ends this state.
    /// Read without the lock: a readiness probe must not wait for a flush in progress.
    /// </summary>
    public bool Faulted => _faulted;

    /// <summary>Opens or creates the journal of <paramref name="round"/> and replays it.</summary>
    public static ResearchLedger Open(string path, Guid round)
    {
        if (round == Guid.Empty)
            throw new ArgumentException("A research ledger needs a round epoch.");
        var journal = new ResearchJournal(path);
        try
        {
            var ledger = new ResearchLedger(journal, round);
            if (journal.Recovered.Count == 0)
                ledger.Require(ledger.Commit([new Entry("round", Round: round), new Entry("format", Version: JournalFormat)]));
            else
            {
                var first = Decode(journal.Recovered[0]);
                if (first.K != "round" || first.Round != round)
                    throw new InvalidDataException($"'{path}' belongs to another round.");
                foreach (var payload in journal.Recovered.Skip(1))
                    ledger.Apply(Decode(payload));
                ledger.RecoveredEntries = journal.Recovered.Count;
                // Everything replayed was understood: only now may the torn tail be cut off. A journal
                // refused above (another round, a newer format, an unknown record) is not modified.
                journal.Repair();
                // A journal of an older build is taken over here, once: from this record on a purchase
                // carries its world effect, and the older build no longer opens the journal.
                if (ledger._format < JournalFormat)
                    ledger.Require(ledger.Commit([new Entry("format", Version: JournalFormat)]));
            }
            return ledger;
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    public void Dispose() => _journal.Dispose();

    /// <summary>
    /// Proves that the journal accepts records now: durably restates its format, a record every build
    /// of this format replays as a no-op. Reading an existing journal shows nothing about the next
    /// append (a full or read-only volume, a locked file); <c>false</c> leaves the ledger faulted.
    /// </summary>
    public bool ConfirmWritable()
    {
        lock (_gate)
            return Commit([new Entry("format", Version: JournalFormat)]) == ResearchStatus.Ok;
    }

    private void Require(ResearchStatus status)
    {
        if (status != ResearchStatus.Ok)
            throw new IOException($"The research journal '{_journal.Path}' cannot be written.");
    }

    // ---- Catalog ---------------------------------------------------------------------------

    /// <summary>
    /// Binds the round to one content version. The first catalog is recorded; afterwards only the
    /// same hash is accepted, and a different one blocks research writes until it matches again.
    /// </summary>
    public ResearchStatus InstallCatalog(ResearchCatalog catalog)
    {
        lock (_gate)
        {
            if (_catalogHash == catalog.Hash)
            {
                _catalog = catalog;
                return ResearchStatus.Duplicate;
            }
            if (_catalogHash is not null)
            {
                _catalog = null;
                return ResearchStatus.CatalogMismatch;
            }
            var status = Commit([new Entry("catalog", Hash: catalog.Hash, Revision: catalog.Nodes.Count)]);
            if (status == ResearchStatus.Ok)
                _catalog = catalog;
            return status;
        }
    }

    // ---- Storage lifecycle -----------------------------------------------------------------

    /// <summary>A new physical server: its own knowledge from the starting nodes and an empty wallet.</summary>
    public (ResearchStatus Status, ResearchStorageView? View) RegisterStorage(string storageId, long generation, string domain)
    {
        if (!ResearchCatalog.IsValidId(storageId) || generation < 1 || !ResearchCatalog.IsValidId(domain))
            return (ResearchStatus.InvalidRequest, null);
        lock (_gate)
        {
            if (_storages.TryGetValue(storageId, out var existing))
                return existing.Generation == generation && existing.Domain == domain && !existing.Destroyed
                    ? (ResearchStatus.Duplicate, View(existing)) : (ResearchStatus.Conflict, View(existing));
            if (_catalog is null)
                return (_catalogHash is null ? ResearchStatus.CatalogUnavailable : ResearchStatus.CatalogMismatch, null);
            if (_storages.Count >= MaxStorages)
                return (ResearchStatus.LimitExceeded, null);
            var starting = _catalog.StartingNodes.Select(node => node.Id).ToArray();
            var status = Commit([new Entry("storage", Storage: storageId, Generation: generation, Domain: domain, Nodes: starting)]);
            return (status, status == ResearchStatus.Ok ? View(_storages[storageId]) : null);
        }
    }

    /// <summary>The storage is physically gone: its knowledge and its points are lost, never moved.</summary>
    public (ResearchStatus Status, ResearchStorageView? View) DestroyStorage(string storageId, long generation)
    {
        lock (_gate)
        {
            if (!_storages.TryGetValue(storageId, out var storage))
                return (ResearchStatus.UnknownStorage, null);
            if (storage.Generation != generation)
                return (ResearchStatus.Conflict, View(storage));
            if (storage.Destroyed)
                return (ResearchStatus.Duplicate, View(storage));
            var status = Commit([new Entry("destroy", Storage: storageId, Generation: generation,
                Points: new Dictionary<string, long>(storage.Available, StringComparer.Ordinal))]);
            return (status, View(storage));
        }
    }

    // ---- Points ----------------------------------------------------------------------------

    public sealed record IncomeGrant(string Storage, long Generation, IReadOnlyDictionary<string, long> PointsMilli);

    /// <summary>
    /// Passive income of one confirmed interval for several servers, under one durable flush.
    /// Each storage takes an interval once; a repeated batch changes nothing.
    /// </summary>
    public ResearchStatus[] GrantIncome(long interval, IReadOnlyList<IncomeGrant> grants)
    {
        var statuses = new ResearchStatus[grants.Count];
        lock (_gate)
        {
            var entries = new List<Entry>();
            var batch = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < grants.Count; i++)
            {
                var grant = grants[i];
                if (interval < 0 || !ValidPoints(grant.PointsMilli))
                {
                    statuses[i] = ResearchStatus.InvalidRequest;
                    continue;
                }
                statuses[i] = Writable(grant.Storage, grant.Generation, out var storage);
                if (statuses[i] != ResearchStatus.Ok)
                    continue;
                if (storage!.Intervals.Contains(interval) || !batch.Add(grant.Storage))
                {
                    statuses[i] = ResearchStatus.Duplicate;
                    continue;
                }
                entries.Add(new Entry("income", Storage: grant.Storage, Generation: grant.Generation, Interval: interval,
                    Points: Copy(grant.PointsMilli)));
            }
            if (entries.Count > 0 && Commit(entries) is var commit && commit != ResearchStatus.Ok)
                for (var i = 0; i < statuses.Length; i++)
                    if (statuses[i] == ResearchStatus.Ok)
                        statuses[i] = commit;
            return statuses;
        }
    }

    /// <summary>
    /// A reward for a research event (analysis, experiment, discovery). The redemption key
    /// <c>{sourceEvent, kind}</c> lives for the whole round: the same evidence is never paid twice,
    /// whichever server it is brought to.
    /// </summary>
    public ResearchStatus Grant(string storageId, long generation, string sourceEvent, string kind,
        IReadOnlyDictionary<string, long> pointsMilli)
    {
        if (!ResearchCatalog.IsValidId(sourceEvent) || !ResearchCatalog.IsValidId(kind) || !ValidPoints(pointsMilli))
            return ResearchStatus.InvalidRequest;
        lock (_gate)
        {
            if (_redemptions.Contains(RedemptionKey(sourceEvent, kind)))
                return ResearchStatus.Duplicate;
            var status = Writable(storageId, generation, out _);
            return status != ResearchStatus.Ok ? status : Commit([new Entry("grant", Storage: storageId, Generation: generation,
                Source: sourceEvent, Kind: kind, Points: Copy(pointsMilli))]);
        }
    }

    /// <summary>
    /// A penalty taken from a wallet, never below zero, once per <c>{sourceEvent, kind}</c>. Only what was
    /// actually there is debited; the debit counts as spent in the conservation totals.
    /// </summary>
    public ResearchStatus Debit(string storageId, long generation, string sourceEvent, string kind,
        IReadOnlyDictionary<string, long> pointsMilli)
    {
        if (!ResearchCatalog.IsValidId(sourceEvent) || !ResearchCatalog.IsValidId(kind) || !ValidPoints(pointsMilli))
            return ResearchStatus.InvalidRequest;
        lock (_gate)
        {
            if (_redemptions.Contains(RedemptionKey(sourceEvent, kind)))
                return ResearchStatus.Duplicate;
            var status = Writable(storageId, generation, out var storage);
            if (status != ResearchStatus.Ok)
                return status;
            var taken = pointsMilli.ToDictionary(pair => pair.Key,
                pair => Math.Min(pair.Value, storage!.Available.GetValueOrDefault(pair.Key)), StringComparer.Ordinal);
            return Commit([new Entry("debit", Storage: storageId, Generation: generation, Source: sourceEvent, Kind: kind, Points: taken)]);
        }
    }

    /// <summary>
    /// Knowledge that arrives without a purchase (a disk, an administrator, a migration): the nodes are
    /// unlocked with no cost and no reward. One import source applies once per storage.
    /// </summary>
    public (ResearchStatus Status, int Added) Import(string storageId, long generation, string source, IReadOnlyList<string> nodes)
    {
        if (!ResearchCatalog.IsValidId(source) || nodes.Count is < 1 or > ResearchCatalog.MaxNodes)
            return (ResearchStatus.InvalidRequest, 0);
        lock (_gate)
        {
            var status = Writable(storageId, generation, out var storage);
            if (status != ResearchStatus.Ok)
                return (status, 0);
            if (_catalog is null)
                return (_catalogHash is null ? ResearchStatus.CatalogUnavailable : ResearchStatus.CatalogMismatch, 0);
            if (nodes.Any(node => !_catalog.Nodes.ContainsKey(node)))
                return (ResearchStatus.UnknownNode, 0);
            if (storage!.Evidence.Contains("import:" + source))
                return (ResearchStatus.Duplicate, 0);
            var added = nodes.Where(node => !storage.Unlocked.Contains(node)).Distinct(StringComparer.Ordinal)
                .OrderBy(node => node, StringComparer.Ordinal).ToArray();
            status = Commit([new Entry("import", Storage: storageId, Generation: generation, Source: source, Nodes: added,
                Revision: added.Length > 0 ? storage.Revision + 1 : storage.Revision)]);
            return (status, status == ResearchStatus.Ok ? added.Length : 0);
        }
    }

    /// <summary>A boost discounts a node by evidence; the same evidence boosts a storage once.</summary>
    public ResearchStatus Boost(string storageId, long generation, string node, string evidence,
        IReadOnlyDictionary<string, long> discount)
    {
        if (!ResearchCatalog.IsValidId(evidence) || discount.Any(pair => !ResearchCatalog.IsValidPointType(pair.Key) ||
                pair.Value is < 0 or > ResearchCatalog.MaxCost))
            return ResearchStatus.InvalidRequest;
        lock (_gate)
        {
            var status = Writable(storageId, generation, out var storage);
            if (status != ResearchStatus.Ok)
                return status;
            if (_catalog is null)
                return _catalogHash is null ? ResearchStatus.CatalogUnavailable : ResearchStatus.CatalogMismatch;
            if (!_catalog.Nodes.ContainsKey(node))
                return ResearchStatus.UnknownNode;
            if (storage!.Evidence.Contains(evidence))
                return ResearchStatus.Duplicate;
            return Commit([new Entry("boost", Storage: storageId, Generation: generation, Node: node, Evidence: evidence,
                Points: Copy(discount), Revision: storage.Revision + 1)]);
        }
    }

    // ---- Purchase --------------------------------------------------------------------------

    /// <summary>
    /// Buys a node on one storage, atomically: prerequisites, the quoted price and the balance are
    /// checked, the exact price is spent and the node unlocked in one durable record. The operation
    /// ID makes the request idempotent for the round; a rejection is recorded the same way, so the
    /// same user action never turns into a purchase later.
    /// <paramref name="payers"/> are other storages of the network that pay what the buyer lacks, in
    /// their order: the network's shared account (decided 12.11.2026). Only living storages of the
    /// buyer's domain count; the price is still the buyer's, by its knowledge and boosts.
    /// </summary>
    public (ResearchStatus Status, ResearchCommandResult Result) Research(string storageId, string operationId,
        string node, long quote, long? expectedRevision = null, IReadOnlyList<string>? payers = null)
    {
        if (!ResearchCatalog.IsValidId(operationId) || !ResearchCatalog.IsValidId(node) || quote < 0)
            return (ResearchStatus.InvalidRequest, new ResearchCommandResult(ResearchOutcome.UnknownNode, node, 0, 0, 0));
        lock (_gate)
        {
            if (_operations.TryGetValue(OperationKey(storageId, operationId), out var recorded))
                return (ResearchStatus.Duplicate, recorded);
            if (!_storages.TryGetValue(storageId, out var storage) || storage.Destroyed)
                return (storage is null ? ResearchStatus.UnknownStorage : ResearchStatus.StorageDestroyed,
                    new ResearchCommandResult(ResearchOutcome.StorageUnavailable, node, storage?.Revision ?? 0, 0, 0));
            if (_catalog is null)
                return (_catalogHash is null ? ResearchStatus.CatalogUnavailable : ResearchStatus.CatalogMismatch,
                    new ResearchCommandResult(ResearchOutcome.CatalogUnavailable, node, storage.Revision, 0, Primary(storage.Available)));
            if (_operations.Count >= MaxOperations)
                return (ResearchStatus.LimitExceeded,
                    new ResearchCommandResult(ResearchOutcome.StorageUnavailable, node, storage.Revision, 0, Primary(storage.Available)));
            var price = new Dictionary<string, long>(StringComparer.Ordinal);
            var contributors = Contributors(storage, payers);
            var outcome = Decide(storage, node, quote, expectedRevision, price, contributors);
            var bought = outcome == ResearchOutcome.Researched;
            var primaryPrice = price.GetValueOrDefault(_catalog.PrimaryPointType);
            var shares = bought ? Split(price, contributors) : null;
            var pooled = contributors.Sum(contributor => Primary(contributor.Available));
            var status = Commit([new Entry("research", Storage: storageId, Generation: storage.Generation, Op: operationId,
                Outcome: ServiceNames.Code(outcome), Node: node, Points: bought ? price : null,
                Revision: bought ? storage.Revision + 1 : storage.Revision, Price: primaryPrice,
                Available: pooled - (bought ? primaryPrice : 0), Payers: shares)]);
            if (status != ResearchStatus.Ok)
                return (status, new ResearchCommandResult(ResearchOutcome.StorageUnavailable, node, storage.Revision, 0, Primary(storage.Available)));
            return (ResearchStatus.Ok, _operations[OperationKey(storageId, operationId)]);
        }
    }

    /// <summary>The price the storage pays now, per point type in milli-points; null for an unknown node.</summary>
    public IReadOnlyDictionary<string, long>? Price(string storageId, string node)
    {
        lock (_gate)
        {
            if (_catalog is null || !_storages.TryGetValue(storageId, out var storage) || !_catalog.Nodes.ContainsKey(node))
                return null;
            var price = new Dictionary<string, long>(StringComparer.Ordinal);
            FillPrice(storage, _catalog.Nodes[node], price);
            return price;
        }
    }

    /// <summary>The buyer first, then the living storages of its domain among the payers, each once.</summary>
    private List<StorageState> Contributors(StorageState buyer, IReadOnlyList<string>? payers)
    {
        var contributors = new List<StorageState> { buyer };
        foreach (var id in payers ?? [])
            if (_storages.TryGetValue(id, out var payer) && !payer.Destroyed && payer.Domain == buyer.Domain &&
                !contributors.Contains(payer))
                contributors.Add(payer);
        return contributors;
    }

    /// <summary>Who pays how much: each point type from the first contributors that have it.</summary>
    private static Dictionary<string, Dictionary<string, long>> Split(Dictionary<string, long> price, List<StorageState> contributors)
    {
        var shares = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var (type, amount) in price)
        {
            var left = amount;
            foreach (var contributor in contributors)
            {
                if (left <= 0)
                    break;
                var take = Math.Min(left, Math.Max(0, contributor.Available.GetValueOrDefault(type)));
                if (take <= 0)
                    continue;
                if (!shares.TryGetValue(contributor.Id, out var share))
                    shares[contributor.Id] = share = new Dictionary<string, long>(StringComparer.Ordinal);
                share[type] = take;
                left -= take;
            }
        }
        return shares;
    }

    /// <summary>What the network's account holds for a storage: its own points and the payers', in milli-points.</summary>
    public long PooledMilli(string storageId, IReadOnlyList<string>? payers, string pointType)
    {
        lock (_gate)
            return _storages.TryGetValue(storageId, out var storage)
                ? Contributors(storage, payers).Sum(contributor => contributor.Available.GetValueOrDefault(pointType))
                : 0;
    }

    private ResearchOutcome Decide(StorageState storage, string nodeId, long quote, long? expectedRevision,
        Dictionary<string, long> price, List<StorageState> contributors)
    {
        if (!_catalog!.Nodes.TryGetValue(nodeId, out var node))
            return ResearchOutcome.UnknownNode;
        if (storage.Unlocked.Contains(nodeId))
            return ResearchOutcome.AlreadyResearched;
        if (expectedRevision is { } revision && revision != storage.Revision)
            return ResearchOutcome.StaleRevision;
        if ((node.Hidden && !storage.Revealed.Contains(nodeId)) || node.Prerequisites.Any(id => !storage.Unlocked.Contains(id)))
            return ResearchOutcome.PrerequisitesMissing;
        FillPrice(storage, node, price);
        if (price.GetValueOrDefault(_catalog.PrimaryPointType) != quote * Milli)
            return ResearchOutcome.QuoteChanged;
        foreach (var (type, amount) in price)
            if (contributors.Sum(contributor => contributor.Available.GetValueOrDefault(type)) < amount)
                return ResearchOutcome.InsufficientPoints;
        return ResearchOutcome.Researched;
    }

    /// <summary>The ledger owns the price: the catalog cost less the best boost, never below zero.</summary>
    private static void FillPrice(StorageState storage, ResearchNodeDefinition node, Dictionary<string, long> price)
    {
        storage.Boosts.TryGetValue(node.Id, out var boost);
        foreach (var (type, cost) in node.Costs)
            price[type] = Math.Max(0, cost - (boost?.GetValueOrDefault(type) ?? 0)) * Milli;
    }

    // ---- World effects of purchases (§13.5) ------------------------------------------------

    /// <summary>The oldest pending effects, at most <paramref name="max"/>. Reading changes nothing: they stay until settled.</summary>
    public IReadOnlyList<ResearchEffect> PendingEffects(int max)
    {
        lock (_gate)
            return _pendingEffects.Values.Take(Math.Max(0, max)).ToArray();
    }

    public ResearchEffectTotals EffectTotals()
    {
        lock (_gate)
            return new ResearchEffectTotals(_pendingEffects.Count, new Dictionary<string, int>(_settledEffects, StringComparer.Ordinal));
    }

    /// <summary>
    /// DM reports how it settled effects: one durable record closes them. An effect is settled once; a
    /// repeated or unknown ID changes nothing and keeps the first result, so a lost reply is safe to repeat.
    /// </summary>
    public (ResearchStatus Status, int Settled) SettleEffects(IReadOnlyList<(string Id, string Result)> results)
    {
        if (results.Count is < 1 or > MaxEffectsPerSettle || results.Any(item => !ResearchCatalog.IsValidId(item.Result)))
            return (ResearchStatus.InvalidRequest, 0);
        lock (_gate)
        {
            var closing = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, result) in results)
                if (_effectOrder.ContainsKey(id))
                    closing.TryAdd(id, result);
            if (closing.Count == 0)
                return (ResearchStatus.Duplicate, 0);
            var status = Commit([new Entry("effect", Effects: closing)]);
            return (status, status == ResearchStatus.Ok ? closing.Count : 0);
        }
    }

    private static string EffectId(string storage, string operation) => storage + "/" + operation;

    // ---- Knowledge sync (§13.6) ------------------------------------------------------------

    /// <summary>What a storage recorded after <paramref name="since"/>, at most <paramref name="maxRecords"/> records.</summary>
    public KnowledgeDelta? Delta(string storageId, long since, int maxRecords)
    {
        lock (_gate)
        {
            if (!_storages.TryGetValue(storageId, out var storage) || storage.Destroyed || maxRecords < 1)
                return null;
            var records = storage.NodeSeq.Where(pair => pair.Value > since).Select(pair => (Seq: pair.Value, Node: pair.Key, Boost: false))
                .Concat(storage.BoostSeq.Where(pair => pair.Value > since).Select(pair => (Seq: pair.Value, Node: pair.Key, Boost: true)))
                .OrderBy(record => record.Seq).ToList();
            var taken = records.Take(maxRecords).ToList();
            var boosts = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
            foreach (var record in taken.Where(record => record.Boost))
                boosts[record.Node] = Copy(storage.Boosts[record.Node]);
            var upto = taken.Count > 0 ? taken[^1].Seq : Math.Max(since, storage.KnowledgeSeq);
            return new KnowledgeDelta(storageId, storage.Revision, upto, records.Count > taken.Count,
                taken.Where(record => !record.Boost).Select(record => record.Node).ToArray(), boosts);
        }
    }

    /// <summary>The last record of <paramref name="peer"/>'s log that <paramref name="storageId"/> already has.</summary>
    public long Watermark(string storageId, string peer)
    {
        lock (_gate)
            return _storages.TryGetValue(storageId, out var storage) ? storage.Peers.GetValueOrDefault(peer) : 0;
    }

    /// <summary>
    /// Knowledge a peer of the same domain sent over the network: unknown nodes are added, boosts merge by the
    /// best discount. Nothing is paid, no reward runs, and replaying a batch, also around a cycle A→B→C→A,
    /// changes nothing. Points, income and charges are never part of it.
    /// </summary>
    public (ResearchStatus Status, int Added) Replicate(string storageId, long generation, string peer, long upto,
        IReadOnlyList<string> nodes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> boosts)
    {
        if (!ResearchCatalog.IsValidId(peer) || peer == storageId || upto < 0 || nodes.Count > ResearchCatalog.MaxNodes ||
            boosts.Count > ResearchCatalog.MaxNodes)
            return (ResearchStatus.InvalidRequest, 0);
        lock (_gate)
        {
            var status = Writable(storageId, generation, out var storage);
            if (status != ResearchStatus.Ok)
                return (status, 0);
            if (!_storages.TryGetValue(peer, out var source))
                return (ResearchStatus.UnknownStorage, 0);
            if (source.Domain != storage!.Domain)
                return (ResearchStatus.Forbidden, 0);
            if (_catalog is null)
                return (_catalogHash is null ? ResearchStatus.CatalogUnavailable : ResearchStatus.CatalogMismatch, 0);
            if (nodes.Concat(boosts.Keys).Any(node => !_catalog.Nodes.ContainsKey(node)))
                return (ResearchStatus.UnknownNode, 0);
            var added = nodes.Where(node => !storage.Unlocked.Contains(node)).Distinct(StringComparer.Ordinal)
                .OrderBy(node => node, StringComparer.Ordinal).ToArray();
            var raised = boosts.Where(pair => storage.Raises(pair.Key, pair.Value))
                .ToDictionary(pair => pair.Key, pair => Copy(pair.Value), StringComparer.Ordinal);
            if (added.Length == 0 && raised.Count == 0 && upto <= storage.Peers.GetValueOrDefault(peer))
                return (ResearchStatus.Duplicate, 0);
            status = Commit([new Entry("replicate", Storage: storageId, Generation: generation, Peer: peer, Upto: upto, Nodes: added,
                Boosts: raised, Revision: added.Length + raised.Count > 0 ? storage.Revision + 1 : storage.Revision)]);
            return (status, status == ResearchStatus.Ok ? added.Length : 0);
        }
    }

    // ---- Transfers (§13.7) -----------------------------------------------------------------

    /// <summary>
    /// Reserve: the points leave the source's available balance for escrow. A repeated reserve of the same
    /// transfer returns it; other content under its ID is a conflict. Nothing reaches the destination yet.
    /// </summary>
    public (ResearchStatus Status, TransferView? View) Reserve(string transfer, string source, long sourceGeneration,
        string destination, IReadOnlyDictionary<string, long> pointsMilli, long deadlineMs)
    {
        if (!ResearchCatalog.IsValidId(transfer) || !ValidPoints(pointsMilli) || pointsMilli.Values.All(amount => amount == 0) ||
            deadlineMs <= 0 || source == destination)
            return (ResearchStatus.InvalidRequest, null);
        lock (_gate)
        {
            if (_transfers.TryGetValue(transfer, out var existing))
                return existing.Source == source && existing.Destination == destination &&
                    existing.Points.Count == pointsMilli.Count && existing.Points.All(pair => pointsMilli.GetValueOrDefault(pair.Key) == pair.Value)
                    ? (ResearchStatus.Duplicate, View(existing)) : (ResearchStatus.Conflict, View(existing));
            var status = Writable(source, sourceGeneration, out var payer);
            if (status != ResearchStatus.Ok)
                return (status, null);
            if (!_storages.TryGetValue(destination, out var payee))
                return (ResearchStatus.UnknownStorage, null);
            if (payee.Destroyed)
                return (ResearchStatus.StorageDestroyed, null);
            if (payee.Domain != payer!.Domain)
                return (ResearchStatus.Forbidden, null);
            if (pointsMilli.Any(pair => payer.Available.GetValueOrDefault(pair.Key) < pair.Value))
                return (ResearchStatus.InsufficientPoints, null);
            if (_transfers.Count >= MaxTransfers)
                return (ResearchStatus.LimitExceeded, null);
            status = Commit([new Entry("reserve", Storage: source, Generation: sourceGeneration, Transfer: transfer, Destination: destination,
                DestinationGeneration: payee.Generation, Points: Copy(pointsMilli), Deadline: deadlineMs)]);
            return (status, status == ResearchStatus.Ok ? View(_transfers[transfer]) : null);
        }
    }

    /// <summary>
    /// Commit: the authorization reached the destination. Escrow moves to its available balance in one
    /// record; a repeated commit returns the same terminal state. A destroyed or regenerated destination
    /// takes nothing, and a transfer past its deadline is settled instead.
    /// </summary>
    public (ResearchStatus Status, TransferView? View) CommitTransfer(string transfer, string destination, long destinationGeneration,
        long nowMs)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transfer, out var record))
                return (ResearchStatus.UnknownTransfer, null);
            if (record.Destination != destination || record.DestinationGeneration != destinationGeneration)
                return (ResearchStatus.Conflict, View(record));
            if (record.State == TransferState.Committed)
                return (ResearchStatus.Duplicate, View(record));
            if (record.State != TransferState.Reserved)
                return (ResearchStatus.TransferClosed, View(record));
            if (nowMs > record.Deadline)
            {
                var settled = Settle(record, TransferState.Expired);
                return (settled == ResearchStatus.Ok ? ResearchStatus.TransferClosed : settled, View(record));
            }
            var status = Writable(destination, destinationGeneration, out _);
            if (status != ResearchStatus.Ok)
                return (status, View(record));
            status = Commit([new Entry("commit", Transfer: transfer)]);
            return (status, View(record));
        }
    }

    /// <summary>Cancels a reserved transfer: the escrow returns to a living source, or is lost with a destroyed one.</summary>
    public (ResearchStatus Status, TransferView? View) CancelTransfer(string transfer)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transfer, out var record))
                return (ResearchStatus.UnknownTransfer, null);
            if (record.State != TransferState.Reserved)
                return (ResearchStatus.Duplicate, View(record));
            return (Settle(record, TransferState.Cancelled), View(record));
        }
    }

    /// <summary>Settles every reserved transfer past its deadline; returns how many expired.</summary>
    public int ExpireTransfers(long nowMs)
    {
        lock (_gate)
        {
            var expired = 0;
            foreach (var record in _transfers.Values.Where(record => record.State == TransferState.Reserved && nowMs > record.Deadline).ToArray())
                if (Settle(record, TransferState.Expired) == ResearchStatus.Ok)
                    expired++;
            return expired;
        }
    }

    public TransferView? Transfer(string transfer)
    {
        lock (_gate)
            return _transfers.TryGetValue(transfer, out var record) ? View(record) : null;
    }

    public IReadOnlyList<TransferView> Transfers()
    {
        lock (_gate)
            return _transfers.Values.OrderBy(record => record.Id, StringComparer.Ordinal).Select(View).ToArray();
    }

    /// <summary>A living source with the same generation gets its escrow back; otherwise the sum is lost, never recreated.</summary>
    private ResearchStatus Settle(TransferRecord record, TransferState requested)
    {
        var refund = _storages.TryGetValue(record.Source, out var source) && !source.Destroyed && source.Generation == record.SourceGeneration;
        return Commit([new Entry("settle", Transfer: record.Id, State: ServiceNames.Code(refund ? requested : TransferState.Lost))]);
    }

    // ---- Disks (§13.8) ---------------------------------------------------------------------

    /// <summary>
    /// Writes the storage's knowledge to a disk as one manifest: nodes, boosts and the catalog hash, never
    /// points. The previous content stays until this record is durable; the same content is a duplicate.
    /// </summary>
    public (ResearchStatus Status, DiskView? View) Backup(string disk, string storageId, long generation)
    {
        if (!ResearchCatalog.IsValidId(disk))
            return (ResearchStatus.InvalidRequest, null);
        lock (_gate)
        {
            var status = Writable(storageId, generation, out var storage);
            if (status != ResearchStatus.Ok)
                return (status, null);
            if (_catalogHash is null)
                return (ResearchStatus.CatalogUnavailable, null);
            var nodes = storage!.Unlocked.OrderBy(node => node, StringComparer.Ordinal).ToArray();
            var boosts = storage.Boosts.ToDictionary(pair => pair.Key, pair => Copy(pair.Value), StringComparer.Ordinal);
            if (_disks.TryGetValue(disk, out var existing) && existing.Source == storageId && existing.Hash == _catalogHash &&
                existing.Nodes.SequenceEqual(nodes) && existing.Boosts.Count == boosts.Count)
                return (ResearchStatus.Duplicate, View(existing));
            if (existing is null && _disks.Count >= MaxDisks)
                return (ResearchStatus.LimitExceeded, null);
            status = Commit([new Entry("backup", Disk: disk, Storage: storageId, Nodes: nodes, Boosts: boosts, Hash: _catalogHash,
                Version: (existing?.Version ?? 0) + 1)]);
            return (status, status == ResearchStatus.Ok ? View(_disks[disk]) : null);
        }
    }

    /// <summary>
    /// Imports a disk into a storage: knowledge it lacks is added, newer knowledge is kept, and no cost,
    /// reward, income or points arrive. A disk of another catalog version is refused. Restoring the same
    /// disk version again changes nothing.
    /// </summary>
    public (ResearchStatus Status, int Added) Restore(string disk, string storageId, long generation)
    {
        lock (_gate)
        {
            if (!_disks.TryGetValue(disk, out var record))
                return (ResearchStatus.UnknownDisk, 0);
            var status = Writable(storageId, generation, out var storage);
            if (status != ResearchStatus.Ok)
                return (status, 0);
            if (record.Hash != _catalogHash)
                return (ResearchStatus.CatalogMismatch, 0);
            if (storage!.Evidence.Contains($"restore:{disk}:{record.Version}"))
                return (ResearchStatus.Duplicate, 0);
            var added = record.Nodes.Where(node => !storage.Unlocked.Contains(node)).ToArray();
            var raised = record.Boosts.Where(pair => storage.Raises(pair.Key, pair.Value))
                .ToDictionary(pair => pair.Key, pair => Copy(pair.Value), StringComparer.Ordinal);
            status = Commit([new Entry("restore", Storage: storageId, Generation: generation, Disk: disk, Version: record.Version,
                Nodes: added, Boosts: raised, Revision: added.Length + raised.Count > 0 ? storage.Revision + 1 : storage.Revision)]);
            return (status, status == ResearchStatus.Ok ? added.Length : 0);
        }
    }

    public DiskView? Disk(string disk)
    {
        lock (_gate)
            return _disks.TryGetValue(disk, out var record) ? View(record) : null;
    }

    // ---- Views -----------------------------------------------------------------------------

    public ResearchStorageView? Storage(string storageId)
    {
        lock (_gate)
            return _storages.TryGetValue(storageId, out var storage) ? View(storage) : null;
    }

    public IReadOnlyList<ResearchStorageView> Storages()
    {
        lock (_gate)
            return _storages.Values.OrderBy(storage => storage.Id, StringComparer.Ordinal).Select(View).ToArray();
    }

    public ResearchTotals Totals()
    {
        lock (_gate)
        {
            var available = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var storage in _storages.Values)
                foreach (var (type, amount) in storage.Available)
                    available[type] = available.GetValueOrDefault(type) + amount;
            return new ResearchTotals(Copy(_minted), available, Copy(_spent), Copy(_lost), Copy(_escrow));
        }
    }

    public int OperationCount { get { lock (_gate) return _operations.Count; } }

    // ---- Journal and replay ----------------------------------------------------------------

    /// <summary>Appends the effects durably, then applies them. Nothing is applied when the append fails.</summary>
    private ResearchStatus Commit(IReadOnlyList<Entry> entries)
    {
        if (_faulted)
            return ResearchStatus.JournalFailed;
        try
        {
            _journal.Append(entries.Select(entry => JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions)).ToArray());
        }
        catch (IOException)
        {
            _faulted = true;
            return ResearchStatus.JournalFailed;
        }
        foreach (var entry in entries)
            Apply(entry);
        return ResearchStatus.Ok;
    }

    private static Entry Decode(byte[] payload) =>
        JsonSerializer.Deserialize<Entry>(payload, JsonOptions) ?? throw new InvalidDataException("Empty research journal record.");

    private void Apply(Entry entry)
    {
        switch (entry.K)
        {
            case "catalog":
                _catalogHash = entry.Hash;
                break;
            case "storage":
                var created = new StorageState { Id = entry.Storage!, Generation = entry.Generation, Domain = entry.Domain!, Revision = 1 };
                foreach (var node in entry.Nodes ?? [])
                    created.Unlock(node);
                _storages[created.Id] = created;
                break;
            case "destroy":
                var destroyed = _storages[entry.Storage!];
                destroyed.Destroyed = true;
                foreach (var (type, amount) in entry.Points ?? [])
                    _lost[type] = _lost.GetValueOrDefault(type) + amount;
                destroyed.Available.Clear();
                break;
            case "income":
                var earner = _storages[entry.Storage!];
                earner.Intervals.Add(entry.Interval);
                Mint(earner, entry.Points);
                break;
            case "grant":
                _redemptions.Add(RedemptionKey(entry.Source!, entry.Kind!));
                Mint(_storages[entry.Storage!], entry.Points);
                break;
            case "debit":
                _redemptions.Add(RedemptionKey(entry.Source!, entry.Kind!));
                var debited = _storages[entry.Storage!];
                foreach (var (type, amount) in entry.Points ?? [])
                {
                    debited.Available[type] = debited.Available.GetValueOrDefault(type) - amount;
                    _spent[type] = _spent.GetValueOrDefault(type) + amount;
                }
                break;
            case "import":
                var importer = _storages[entry.Storage!];
                importer.Evidence.Add("import:" + entry.Source);
                foreach (var node in entry.Nodes ?? [])
                    importer.Unlock(node);
                importer.Revision = entry.Revision;
                break;
            case "boost":
                var boosted = _storages[entry.Storage!];
                boosted.Evidence.Add(entry.Evidence!);
                boosted.MergeBoost(entry.Node!, entry.Points);
                boosted.Revision = entry.Revision;
                break;
            case "research":
                var buyer = _storages[entry.Storage!];
                var outcome = Enum.Parse<ResearchOutcome>(entry.Outcome!.Replace("_", ""), ignoreCase: true);
                if (outcome == ResearchOutcome.Researched)
                {
                    buyer.Unlock(entry.Node!);
                    // A record without payers is the buyer's alone (journals before the shared account).
                    var shares = entry.Payers ?? new Dictionary<string, Dictionary<string, long>> { [entry.Storage!] = entry.Points ?? [] };
                    foreach (var (payerId, share) in shares)
                    {
                        var payerState = _storages[payerId];
                        foreach (var (type, amount) in share)
                        {
                            payerState.Available[type] = payerState.Available.GetValueOrDefault(type) - amount;
                            _spent[type] = _spent.GetValueOrDefault(type) + amount;
                        }
                    }
                }
                buyer.Revision = entry.Revision;
                _operations[OperationKey(entry.Storage!, entry.Op!)] =
                    new ResearchCommandResult(outcome, entry.Node!, entry.Revision, entry.Price, entry.Available);
                // The obligation to the world is this same record: there is no state in which the purchase
                // is durable and its effect is not.
                if (outcome == ResearchOutcome.Researched && _format >= 2)
                {
                    var effect = new ResearchEffect(EffectId(entry.Storage!, entry.Op!), ResearchEffect.Researched, entry.Storage!,
                        entry.Op!, entry.Node!, entry.Price, entry.Revision);
                    _effectOrder[effect.Id] = ++_effectSeq;
                    _pendingEffects[_effectSeq] = effect;
                }
                break;
            case "effect":
                foreach (var (id, result) in entry.Effects ?? [])
                    if (_effectOrder.Remove(id, out var order))
                    {
                        _pendingEffects.Remove(order);
                        _settledEffects[result] = _settledEffects.GetValueOrDefault(result) + 1;
                    }
                break;
            case "format":
                if (entry.Version > JournalFormat)
                    throw new InvalidDataException($"'{_journal.Path}' was written by a newer netDM (journal format {entry.Version}, " +
                        $"this build reads up to {JournalFormat}).");
                _format = Math.Max(_format, (int)entry.Version);
                break;
            case "replicate":
                var replica = _storages[entry.Storage!];
                foreach (var node in entry.Nodes ?? [])
                    replica.Unlock(node);
                foreach (var (node, discount) in entry.Boosts ?? [])
                    replica.MergeBoost(node, discount);
                replica.Peers[entry.Peer!] = Math.Max(replica.Peers.GetValueOrDefault(entry.Peer!), entry.Upto);
                replica.Revision = entry.Revision;
                break;
            case "reserve":
                var payer = _storages[entry.Storage!];
                var reserved = new TransferRecord
                {
                    Id = entry.Transfer!, Source = entry.Storage!, SourceGeneration = entry.Generation, Destination = entry.Destination!,
                    DestinationGeneration = entry.DestinationGeneration, Deadline = entry.Deadline, State = TransferState.Reserved,
                    Points = new Dictionary<string, long>(entry.Points ?? [], StringComparer.Ordinal)
                };
                foreach (var (type, amount) in reserved.Points)
                {
                    payer.Available[type] = payer.Available.GetValueOrDefault(type) - amount;
                    _escrow[type] = _escrow.GetValueOrDefault(type) + amount;
                }
                _transfers[reserved.Id] = reserved;
                break;
            case "commit":
                var committed = _transfers[entry.Transfer!];
                var payee = _storages[committed.Destination];
                foreach (var (type, amount) in committed.Points)
                {
                    _escrow[type] = _escrow.GetValueOrDefault(type) - amount;
                    payee.Available[type] = payee.Available.GetValueOrDefault(type) + amount;
                }
                committed.State = TransferState.Committed;
                break;
            case "settle":
                var settled = _transfers[entry.Transfer!];
                var state = Enum.Parse<TransferState>(entry.State!, ignoreCase: true);
                foreach (var (type, amount) in settled.Points)
                {
                    _escrow[type] = _escrow.GetValueOrDefault(type) - amount;
                    if (state == TransferState.Lost)
                        _lost[type] = _lost.GetValueOrDefault(type) + amount;
                    else
                        _storages[settled.Source].Available[type] = _storages[settled.Source].Available.GetValueOrDefault(type) + amount;
                }
                settled.State = state;
                break;
            case "backup":
                _disks[entry.Disk!] = new DiskRecord
                {
                    Id = entry.Disk!, Source = entry.Storage!, Version = entry.Version, Hash = entry.Hash!, Nodes = entry.Nodes ?? [],
                    Boosts = entry.Boosts ?? new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal)
                };
                break;
            case "restore":
                var restored = _storages[entry.Storage!];
                restored.Evidence.Add($"restore:{entry.Disk}:{entry.Version}");
                foreach (var node in entry.Nodes ?? [])
                    restored.Unlock(node);
                foreach (var (node, discount) in entry.Boosts ?? [])
                    restored.MergeBoost(node, discount);
                restored.Revision = entry.Revision;
                break;
            case "round":
                break;
            default:
                throw new InvalidDataException($"Unknown research journal record '{entry.K}'.");
        }
    }

    private void Mint(StorageState storage, Dictionary<string, long>? points)
    {
        foreach (var (type, amount) in points ?? [])
        {
            storage.Available[type] = storage.Available.GetValueOrDefault(type) + amount;
            _minted[type] = _minted.GetValueOrDefault(type) + amount;
        }
    }

    private ResearchStatus Writable(string storageId, long generation, out StorageState? storage)
    {
        if (!_storages.TryGetValue(storageId, out storage))
            return ResearchStatus.UnknownStorage;
        if (storage.Generation != generation)
            return ResearchStatus.Conflict;
        return storage.Destroyed ? ResearchStatus.StorageDestroyed : ResearchStatus.Ok;
    }

    private long Primary(Dictionary<string, long> values) =>
        _catalog is null ? values.Values.FirstOrDefault() : values.GetValueOrDefault(_catalog.PrimaryPointType);

    private static bool ValidPoints(IReadOnlyDictionary<string, long> points) =>
        points.Count is > 0 and <= 16 && points.All(pair => ResearchCatalog.IsValidPointType(pair.Key) && pair.Value is >= 0 and <= MaxGrantMilli);

    private static Dictionary<string, long> Copy(IReadOnlyDictionary<string, long> values) =>
        new(values, StringComparer.Ordinal);

    private static string OperationKey(string storage, string operation) => storage + "\n" + operation;
    private static string RedemptionKey(string source, string kind) => source + "\n" + kind;

    private static TransferView View(TransferRecord record) => new(record.Id, record.Source, record.Destination, Copy(record.Points),
        record.State, record.Deadline);

    private static DiskView View(DiskRecord record) => new(record.Id, record.Source, record.Version, record.Nodes.Length, record.Hash);

    private static ResearchStorageView View(StorageState storage) => new(storage.Id, storage.Generation, storage.Domain,
        storage.Destroyed, storage.Revision, storage.Unlocked.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        Copy(storage.Available), storage.Intervals.Count, storage.KnowledgeSeq);
}

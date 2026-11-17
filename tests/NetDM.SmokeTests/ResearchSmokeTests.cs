using NetDM.Services.Research;

/// <summary>
/// P5 research ledger: per-server wallets and knowledge, atomic purchases with quotes, idempotent
/// income and rewards, durable recovery, and the conservation invariant under crash injection.
/// </summary>
static class ResearchSmokeTests
{
    private const string General = "General Research";

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netdm-research-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckCatalog();
            CheckPurchases(directory);
            CheckConcurrentSpend(directory);
            CheckRestartAndCatalogBinding(directory);
            CheckSync(directory);
            CheckTransfers(directory);
            CheckDisks(directory);
            CheckCrashInjection(directory);
            CheckSharedAccount(directory);
            CheckEffects(directory);
            CheckJournalFormat(directory);
            CheckJournalDamage(directory);
            CheckWriteFault(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Console.WriteLine("P5 research ledger, purchase outbox, journal format, journal damage and write fault, P6 sync, transfer and disk checks passed.");
    }

    private static ResearchNodeDefinition Node(string id, long cost, string[]? prerequisites = null, bool starting = false,
        bool hidden = false) =>
        new(id, prerequisites ?? [], [id + "_design"], new Dictionary<string, long> { [General] = cost }, starting, hidden);

    private static ResearchCatalog Catalog(long advancedCost = 500) => ResearchCatalog.Create(
    [
        Node("base", 0, starting: true), Node("engineering", 300, ["base"]), Node("advanced", advancedCost, ["engineering"]),
        Node("secret", 200, ["base"], hidden: true), Node("cheap", 100, ["base"])
    ], [General]);

    private static Dictionary<string, long> Points(long whole) => new() { [General] = whole * ResearchLedger.Milli };

    private static void CheckCatalog()
    {
        var catalog = Catalog();
        var reordered = ResearchCatalog.Create(catalog.Nodes.Values.Reverse(), [General]);
        Check(catalog.Hash == reordered.Hash && catalog.Hash.Length == 64, "catalog hash ignores order");
        Check(catalog.Hash != Catalog(600).Hash, "a changed price changes the hash");
        Reject(() => ResearchCatalog.Create([Node("a", 1, ["missing"])], [General]), "unknown prerequisite");
        Reject(() => ResearchCatalog.Create([Node("a", 1), Node("a", 2)], [General]), "duplicate node");
        Reject(() => ResearchCatalog.Create([Node("a b", 1)], [General]), "invalid node ID");
        Check(ResearchCatalog.Create([new ResearchNodeDefinition("a", [], ["power control"], new Dictionary<string, long>())], [General])
            .Nodes["a"].Designs.Single() == "power control", "design IDs are opaque text");
        Reject(() => ResearchCatalog.Create([new ResearchNodeDefinition("a", [], ["tab\there"], new Dictionary<string, long>())], [General]),
            "control characters in a design ID");
        Reject(() => ResearchCatalog.Create([new ResearchNodeDefinition("a", [], [], new Dictionary<string, long> { ["Other"] = 1 })],
            [General]), "unknown point type");
    }

    private static void CheckPurchases(string directory)
    {
        using var ledger = ResearchLedger.Open(Path.Combine(directory, "purchases.journal"), Guid.NewGuid());
        Check(ledger.RegisterStorage("srv-a", 1, "station").Status == ResearchStatus.CatalogUnavailable, "storage needs the catalog");
        Check(ledger.InstallCatalog(Catalog()) == ResearchStatus.Ok, "catalog installed");
        var (status, view) = ledger.RegisterStorage("srv-a", 1, "station");
        Check(status == ResearchStatus.Ok && view!.Unlocked.SequenceEqual(["base"]) && view.Revision == 1, "starting knowledge");
        Check(ledger.RegisterStorage("srv-a", 1, "station").Status == ResearchStatus.Duplicate, "registration is idempotent");
        Check(ledger.RegisterStorage("srv-a", 2, "station").Status == ResearchStatus.Conflict, "an ID is one physical storage");
        Check(ledger.RegisterStorage("srv-b", 1, "station").Status == ResearchStatus.Ok, "second server");

        // Income goes to the server that mined it, once per interval.
        var grants = ledger.GrantIncome(10, [new("srv-a", 1, Points(400)), new("srv-b", 1, Points(50))]);
        Check(grants.All(s => s == ResearchStatus.Ok), "income batch");
        Check(ledger.GrantIncome(10, [new("srv-a", 1, Points(400))]).Single() == ResearchStatus.Duplicate, "an interval pays once");
        Check(ledger.GrantIncome(11, [new("srv-a", 2, Points(1))]).Single() == ResearchStatus.Conflict, "generation fence");
        Check(Available(ledger, "srv-a") == 400 && Available(ledger, "srv-b") == 50, "wallets are separate");

        Check(Buy(ledger, "srv-a", "op-1", "advanced", 500).Outcome == ResearchOutcome.PrerequisitesMissing, "prerequisites");
        Check(Buy(ledger, "srv-a", "op-2", "secret", 200).Outcome == ResearchOutcome.PrerequisitesMissing, "hidden node");
        Check(Buy(ledger, "srv-a", "op-3", "engineering", 250).Outcome == ResearchOutcome.QuoteChanged, "stale quote spends nothing");
        Check(Buy(ledger, "srv-a", "op-4", "engineering", 300, expectedRevision: 7).Outcome == ResearchOutcome.StaleRevision,
            "stale knowledge revision");
        var bought = Buy(ledger, "srv-a", "op-5", "engineering", 300, expectedRevision: 1);
        Check(bought is { Outcome: ResearchOutcome.Researched, Revision: 2, Price: 300, Available: 100 }, "purchase");
        var repeated = ledger.Research("srv-a", "op-5", "engineering", 300);
        Check(repeated.Status == ResearchStatus.Duplicate && repeated.Result == bought, "a repeated operation returns its answer");
        Check(Buy(ledger, "srv-a", "op-6", "engineering", 300).Outcome == ResearchOutcome.AlreadyResearched, "no second unlock");
        Check(Buy(ledger, "srv-a", "op-7", "advanced", 500).Outcome == ResearchOutcome.InsufficientPoints, "balance");
        Check(Buy(ledger, "srv-b", "op-8", "engineering", 300).Outcome == ResearchOutcome.InsufficientPoints,
            "server B cannot spend server A's points");
        Check(Buy(ledger, "srv-a", "op-9", "missing", 1).Outcome == ResearchOutcome.UnknownNode, "unknown node");
        Check(ledger.Storage("srv-b")!.Unlocked.SequenceEqual(["base"]), "knowledge stays on its server");

        // A boost discounts by evidence once; it also reveals a hidden node.
        Check(ledger.Boost("srv-a", 1, "secret", "scan-1", new Dictionary<string, long> { [General] = 150 }) == ResearchStatus.Ok,
            "boost");
        Check(ledger.Boost("srv-a", 1, "secret", "scan-1", new Dictionary<string, long> { [General] = 190 }) == ResearchStatus.Duplicate,
            "evidence boosts once");
        Check(ledger.Price("srv-a", "secret")![General] == 50 * ResearchLedger.Milli, "the ledger owns the price");
        Check(Buy(ledger, "srv-a", "op-10", "secret", 50).Outcome == ResearchOutcome.Researched, "boosted purchase");

        // A reward is redeemed once for the round, whichever server takes the evidence.
        Check(ledger.Grant("srv-a", 1, "analyzer-17", "deconstruct", Points(70)) == ResearchStatus.Ok, "reward");
        Check(ledger.Grant("srv-b", 1, "analyzer-17", "deconstruct", Points(70)) == ResearchStatus.Duplicate, "no second reward");

        // A penalty never takes more than is there; an import unlocks without a charge, once per source.
        Check(ledger.Debit("srv-a", 1, "ballmer-1", "penalty", Points(1000)) == ResearchStatus.Ok && Available(ledger, "srv-a") == 0,
            "a debit stops at zero");
        Check(ledger.Debit("srv-a", 1, "ballmer-1", "penalty", Points(5)) == ResearchStatus.Duplicate, "a debit applies once");
        ledger.GrantIncome(13, [new("srv-a", 1, Points(120))]);
        Check(ledger.Import("srv-a", 1, "disk-1", ["advanced", "cheap"]) == (ResearchStatus.Ok, 2) &&
            Available(ledger, "srv-a") == 120 && ledger.Storage("srv-a")!.Unlocked.Contains("advanced"), "an import costs nothing");
        Check(ledger.Import("srv-a", 1, "disk-1", ["advanced"]).Status == ResearchStatus.Duplicate, "an import source applies once");
        Check(ledger.Import("srv-a", 1, "disk-2", ["nope"]).Status == ResearchStatus.UnknownNode, "an import needs catalog nodes");

        // Destruction loses the points; nothing reappears elsewhere.
        Check(ledger.DestroyStorage("srv-b", 1).Status == ResearchStatus.Ok, "destroy");
        Check(ledger.DestroyStorage("srv-b", 1).Status == ResearchStatus.Duplicate, "destroy is idempotent");
        Check(ledger.GrantIncome(12, [new("srv-b", 1, Points(5))]).Single() == ResearchStatus.StorageDestroyed, "no income after destroy");
        var totals = ledger.Totals();
        // Spent: 300 + 50 in purchases and the 120 the debit found (400 - 300 - 50 + 70 reward).
        Check(totals.Balanced && totals.Lost[General] == 50 * ResearchLedger.Milli && totals.Spent[General] == 470 * ResearchLedger.Milli,
            "minted = available + spent + lost");
    }

    private static void CheckConcurrentSpend(string directory)
    {
        using var ledger = ResearchLedger.Open(Path.Combine(directory, "race.journal"), Guid.NewGuid());
        ledger.InstallCatalog(Catalog());
        ledger.RegisterStorage("srv", 1, "station");
        ledger.GrantIncome(1, [new("srv", 1, Points(300))]);
        // Two consoles race for the last 300 points with two different nodes.
        var results = new ResearchCommandResult[2];
        Parallel.For(0, 2, i => results[i] = ledger.Research("srv", "race-" + i, i == 0 ? "engineering" : "cheap", i == 0 ? 300 : 100).Result);
        var winners = results.Count(r => r.Outcome == ResearchOutcome.Researched);
        Check(winners >= 1 && Available(ledger, "srv") >= 0 && ledger.Totals().Balanced, "one writer serializes spends");
        Check(!(results[0].Outcome == ResearchOutcome.Researched && results[1].Outcome == ResearchOutcome.Researched),
            "the last points are not spent twice");
    }

    private static void CheckRestartAndCatalogBinding(string directory)
    {
        var path = Path.Combine(directory, "restart.journal");
        var round = Guid.NewGuid();
        ResearchCommandResult first;
        using (var ledger = ResearchLedger.Open(path, round))
        {
            ledger.InstallCatalog(Catalog());
            ledger.RegisterStorage("srv", 1, "station");
            ledger.GrantIncome(1, [new("srv", 1, Points(1000))]);
            first = Buy(ledger, "srv", "op-1", "engineering", 300);
        }
        using (var reopened = ResearchLedger.Open(path, round))
        {
            Check(reopened.RecoveredEntries == 6 && reopened.TruncatedBytes == 0, "journal replayed");
            Check(Available(reopened, "srv") == 700 && reopened.Storage("srv")!.Unlocked.Contains("engineering"), "state survives restart");
            var repeated = reopened.Research("srv", "op-1", "engineering", 300);
            Check(repeated.Status == ResearchStatus.Duplicate && repeated.Result == first, "a retried request after restart does not spend");
            Check(Buy(reopened, "srv", "op-2", "advanced", 500).Outcome == ResearchOutcome.CatalogUnavailable, "writes wait for the catalog");
            Check(reopened.InstallCatalog(Catalog(600)) == ResearchStatus.CatalogMismatch, "another content version is refused");
            Check(Buy(reopened, "srv", "op-3", "advanced", 600).Outcome == ResearchOutcome.CatalogUnavailable, "mismatch blocks writes");
            Check(reopened.InstallCatalog(Catalog()) == ResearchStatus.Duplicate, "the recorded catalog is accepted again");
            Check(Buy(reopened, "srv", "op-4", "advanced", 500).Outcome == ResearchOutcome.Researched, "writes resume");
            Check(reopened.GrantIncome(1, [new("srv", 1, Points(1000))]).Single() == ResearchStatus.Duplicate,
                "income of a recorded interval is not paid again after restart");
        }
        Reject(() => ResearchLedger.Open(path, Guid.NewGuid()).Dispose(), "a journal belongs to one round");
    }

    /// <summary>
    /// The network's shared account: a purchase on one server is paid by it first and then by the other
    /// servers of its domain that the Host passes as payers, in one durable record. Another domain and a
    /// destroyed server never pay; a shortfall buys nothing; the split survives a restart.
    /// </summary>
    private static void CheckSharedAccount(string directory)
    {
        var path = Path.Combine(directory, "shared.journal");
        var round = Guid.NewGuid();
        using (var ledger = ResearchLedger.Open(path, round))
        {
            ledger.InstallCatalog(Catalog());
            foreach (var (id, domain) in new[] { ("srv-a", "station"), ("srv-b", "station"), ("srv-c", "station"), ("srv-x", "web-x") })
                ledger.RegisterStorage(id, 1, domain);
            ledger.GrantIncome(1, [new("srv-a", 1, Points(100)), new("srv-b", 1, Points(150)), new("srv-c", 1, Points(400)),
                new("srv-x", 1, Points(1000))]);
            string[] payers = ["srv-x", "srv-b", "srv-c"];
            Check(ledger.PooledMilli("srv-a", payers, General) == 650 * ResearchLedger.Milli, "the account holds its domain only");
            var bought = ledger.Research("srv-a", "op-1", "engineering", 300, null, payers).Result;
            Check(bought.Outcome == ResearchOutcome.Researched && bought.Available == 350, "the network pays what the server lacks");
            Check(Available(ledger, "srv-a") == 0 && Available(ledger, "srv-b") == 0 && Available(ledger, "srv-c") == 350 &&
                Available(ledger, "srv-x") == 1000, "the buyer first, then the payers in their order; another domain never pays");
            Check(ledger.Storage("srv-a")!.Unlocked.Contains("engineering") && !ledger.Storage("srv-c")!.Unlocked.Contains("engineering"),
                "the knowledge is the buyer's");
            Check(ledger.Research("srv-a", "op-2", "advanced", 500, null, payers).Result.Outcome == ResearchOutcome.InsufficientPoints &&
                Available(ledger, "srv-c") == 350, "a shortfall buys nothing and spends nothing");
            ledger.DestroyStorage("srv-c", 1);
            Check(ledger.Research("srv-a", "op-3", "cheap", 100, null, payers).Result.Outcome == ResearchOutcome.InsufficientPoints,
                "a destroyed server pays nothing");
            Check(ledger.Research("srv-a", "op-1", "engineering", 300, null, payers).Status == ResearchStatus.Duplicate,
                "a retried purchase is the recorded one");
            Check(ledger.Totals().Balanced, "points are conserved");
        }
        using var replayed = ResearchLedger.Open(path, round);
        replayed.InstallCatalog(Catalog());
        Check(Available(replayed, "srv-a") == 0 && Available(replayed, "srv-b") == 0 && Available(replayed, "srv-x") == 1000 &&
            replayed.Storage("srv-a")!.Unlocked.Contains("engineering") && replayed.Totals().Balanced, "the split replays from the journal");
    }

    /// <summary>
    /// The outbox of purchases (§13.5): a purchase and what it owes the world are one journal record. The
    /// obligation is delivered under a stable ID until DM settles it, also by the next Host process, and is
    /// settled once. A refusal, a retried purchase and knowledge that arrives without a purchase owe nothing.
    /// </summary>
    private static void CheckEffects(string directory)
    {
        var path = Path.Combine(directory, "effects.journal");
        var round = Guid.NewGuid();
        using (var ledger = ResearchLedger.Open(path, round))
        {
            ledger.InstallCatalog(Catalog());
            ledger.RegisterStorage("srv", 1, "station");
            ledger.GrantIncome(1, [new("srv", 1, Points(1000))]);
            Check(Buy(ledger, "srv", "op-1", "advanced", 500).Outcome == ResearchOutcome.PrerequisitesMissing &&
                Buy(ledger, "srv", "op-2", "engineering", 299).Outcome == ResearchOutcome.QuoteChanged &&
                ledger.EffectTotals().Pending == 0, "a refused purchase owes the world nothing");
            var bought = Buy(ledger, "srv", "op-3", "engineering", 300);
            Check(bought.Outcome == ResearchOutcome.Researched && ledger.PendingEffects(8).Single() ==
                new ResearchEffect("srv/op-3", ResearchEffect.Researched, "srv", "op-3", "engineering", 300 * ResearchLedger.Milli, bought.Revision),
                "a purchase carries its world effect");
            Check(ledger.Research("srv", "op-3", "engineering", 300).Status == ResearchStatus.Duplicate &&
                Buy(ledger, "srv", "op-3b", "engineering", 300).Outcome == ResearchOutcome.AlreadyResearched &&
                ledger.EffectTotals().Pending == 1, "a retried purchase owes nothing more");
            Buy(ledger, "srv", "op-4", "cheap", 100);
            ledger.Import("srv", 1, "disk", ["secret"]);
            Check(ledger.PendingEffects(1).Single().Id == "srv/op-3" && ledger.PendingEffects(8).Count == 2,
                "effects wait oldest first in a bounded page; knowledge without a purchase adds none");
            Check(ledger.PendingEffects(8).Count == 2 && ledger.EffectTotals().Pending == 2, "reading the outbox settles nothing");
        }
        // The Host died before DM reported anything: the same obligations wait under the same IDs.
        using (var reopened = ResearchLedger.Open(path, round))
        {
            Check(reopened.PendingEffects(8).Select(effect => effect.Id).SequenceEqual(["srv/op-3", "srv/op-4"]),
                "pending effects survive a restart");
            Check(reopened.SettleEffects([("srv/op-3", "paid")]) == (ResearchStatus.Ok, 1), "DM settles an effect");
            Check(reopened.SettleEffects([("srv/op-3", "void"), ("srv/unknown", "paid")]) == (ResearchStatus.Duplicate, 0) &&
                reopened.EffectTotals() is { Pending: 1 } totals && totals.Settled.GetValueOrDefault("paid") == 1 &&
                !totals.Settled.ContainsKey("void"), "a repeated or unknown settlement changes nothing and keeps the first result");
            Check(reopened.SettleEffects([]).Status == ResearchStatus.InvalidRequest &&
                reopened.SettleEffects([("srv/op-4", "not a code")]).Status == ResearchStatus.InvalidRequest &&
                reopened.SettleEffects(Enumerable.Repeat(("srv/op-4", "paid"), ResearchLedger.MaxEffectsPerSettle + 1).ToArray()).Status ==
                ResearchStatus.InvalidRequest && reopened.EffectTotals().Pending == 1, "a malformed settlement is refused whole");
            reopened.DestroyStorage("srv", 1);
            Check(reopened.PendingEffects(8).Single().Id == "srv/op-4", "a destroyed storage keeps its obligation until the world settles it");
        }
        using (var settled = ResearchLedger.Open(path, round))
        {
            Check(settled.PendingEffects(8).Single().Id == "srv/op-4" && settled.EffectTotals().Settled.GetValueOrDefault("paid") == 1,
                "a settlement survives a restart");
            Check(settled.SettleEffects([("srv/op-4", "void"), ("srv/op-4", "paid")]) == (ResearchStatus.Ok, 1) &&
                settled.EffectTotals() is { Pending: 0 } totals && totals.Settled.GetValueOrDefault("void") == 1 &&
                totals.Settled.GetValueOrDefault("paid") == 1, "one effect settles once inside one report too");
            Check(settled.Totals().Balanced, "settling moves no points");
        }
    }

    /// <summary>
    /// Upgrade and rollback of the Host over a round's journal. A journal of the build before effects is
    /// taken over once: its state is kept and its old purchases owe nothing. A journal of a newer format,
    /// or with a record this build does not know, is refused and left untouched: research stays blocked
    /// instead of continuing on misread records.
    /// </summary>
    private static void CheckJournalFormat(string directory)
    {
        var round = Guid.NewGuid();
        byte[] Record(object value) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        // Format 1, as written before the format record existed.
        var older = Path.Combine(directory, "format-1.journal");
        using (var journal = new ResearchJournal(older))
            journal.Append([
                Record(new { k = "round", round }),
                Record(new { k = "catalog", hash = Catalog().Hash, revision = 5 }),
                Record(new { k = "storage", storage = "srv", generation = 1, domain = "station", nodes = new[] { "base" } }),
                Record(new { k = "income", storage = "srv", generation = 1, interval = 1, points = Points(1000) }),
                Record(new { k = "research", storage = "srv", generation = 1, op = "old-1", outcome = "researched", node = "engineering",
                    points = Points(300), revision = 2, price = 300 * ResearchLedger.Milli, available = 700 * ResearchLedger.Milli })
            ]);
        using (var upgraded = ResearchLedger.Open(older, round))
        {
            Check(upgraded.Format == ResearchLedger.JournalFormat && upgraded.RecoveredEntries == 5, "an older journal is taken over");
            Check(Available(upgraded, "srv") == 700 && upgraded.Storage("srv")!.Unlocked.Contains("engineering") &&
                upgraded.Totals().Balanced, "its state is kept");
            upgraded.InstallCatalog(Catalog());
            Check(upgraded.Research("srv", "old-1", "engineering", 300).Status == ResearchStatus.Duplicate &&
                upgraded.EffectTotals().Pending == 0, "a purchase of the older protocol owes nothing: its effect was applied then");
            Check(Buy(upgraded, "srv", "new-1", "cheap", 100).Outcome == ResearchOutcome.Researched &&
                upgraded.PendingEffects(8).Single().Id == "srv/new-1", "a purchase after the takeover carries its effect");
        }
        using (var again = ResearchLedger.Open(older, round))
            Check(again.RecoveredEntries == 7 && again.PendingEffects(8).Single().Id == "srv/new-1" && Available(again, "srv") == 600,
                "the takeover is recorded once");

        foreach (var (name, record) in new[]
                 {
                     ("format-newer", Record(new { k = "format", version = ResearchLedger.JournalFormat + 1 })),
                     ("format-unknown", Record(new { k = "from_a_newer_build", storage = "srv" }))
                 })
        {
            var newer = Path.Combine(directory, name + ".journal");
            using (var journal = new ResearchJournal(newer))
                journal.Append([Record(new { k = "round", round }), Record(new { k = "format", version = ResearchLedger.JournalFormat }), record]);
            Tear(newer);
            var before = File.ReadAllBytes(newer);
            Reject(() => ResearchLedger.Open(newer, round).Dispose(), $"a journal this build cannot read ({name})");
            Check(File.ReadAllBytes(newer).AsSpan().SequenceEqual(before), "a refused journal is left untouched, its torn tail included");
        }

        // Another round's journal is refused the same way: nothing of it is cut off.
        var foreign = Path.Combine(directory, "format-foreign.journal");
        using (var journal = new ResearchJournal(foreign))
            journal.Append([Record(new { k = "round", round = Guid.NewGuid() }), Record(new { k = "format", version = ResearchLedger.JournalFormat })]);
        Tear(foreign);
        var foreignBefore = File.ReadAllBytes(foreign);
        Reject(() => ResearchLedger.Open(foreign, round).Dispose(), "another round's journal");
        Check(File.ReadAllBytes(foreign).AsSpan().SequenceEqual(foreignBefore), "another round's journal is left untouched");
    }

    /// <summary>
    /// The journal's device stops accepting writes (§17.4): nothing is applied or written from then on,
    /// and only replaying the journal brings the ledger back, with exactly what the disk holds.
    /// </summary>
    private static void CheckWriteFault(string directory)
    {
        var path = Path.Combine(directory, "write-fault.journal");
        var round = Guid.NewGuid();
        var ledger = ResearchLedger.Open(path, round);
        try
        {
            ledger.InstallCatalog(Catalog());
            ledger.RegisterStorage("srv", 1, "station");
            ledger.GrantIncome(1, [new("srv", 1, Points(1000))]);
            Check(ledger.ConfirmWritable() && !ledger.Faulted && Available(ledger, "srv") == 1000, "a healthy journal confirms a write");
            var before = Fingerprint(ledger, []);
            ledger.Dispose();
            ledger = ResearchLedger.Open(path, round);
            ledger.InstallCatalog(Catalog());
            Check(ledger.RecoveredEntries == 6 && ledger.Format == ResearchLedger.JournalFormat && Fingerprint(ledger, []) == before,
                "the confirmation replays as nothing");

            // A byte-range lock held through another handle refuses the write itself. It is mandatory
            // only on Windows; elsewhere the lock is advisory and a refused write cannot be staged here.
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Research write fault: injection skipped, it needs mandatory file locks (Windows).");
                return;
            }
            var length = new FileInfo(path).Length;
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                blocker.Lock(length, long.MaxValue - length);
                var refused = ledger.Research("srv", "buy-1", "engineering", 300);
                Check(refused.Status == ResearchStatus.JournalFailed && refused.Result.Outcome == ResearchOutcome.StorageUnavailable &&
                    ledger.Faulted, "a refused write fails the purchase and faults the ledger");
                Check(Available(ledger, "srv") == 1000 && ledger.EffectTotals().Pending == 0 && Fingerprint(ledger, []) == before,
                    "a failed append applies nothing");
                // A retry while the device still refuses writes: the journal is read, never reported writable.
                ledger.Dispose();
                ledger = ResearchLedger.Open(path, round);
                ledger.InstallCatalog(Catalog());
                Check(Fingerprint(ledger, []) == before && !ledger.Faulted && !ledger.ConfirmWritable() && ledger.Faulted,
                    "replay reads the journal while its device refuses writes, and the write test fails");
            }
            // The device is back, but the outcome of the failed append is unknown to this ledger.
            Check(ledger.Grant("srv", 1, "reward-1", "deconstruct", Points(5)) == ResearchStatus.JournalFailed && !ledger.ConfirmWritable() &&
                ledger.SettleEffects([("srv/buy-1", "paid")]).Status != ResearchStatus.Ok, "a faulted ledger writes nothing until it is reopened");
            Check(new FileInfo(path).Length == length, "nothing of the refused write reaches the file later");
            ledger.Dispose();
            Check(new FileInfo(path).Length == length, "closing the faulted journal writes nothing either");

            ledger = ResearchLedger.Open(path, round);
            ledger.InstallCatalog(Catalog());
            Check(!ledger.Faulted && ledger.ConfirmWritable() && Fingerprint(ledger, []) == before, "the reopened ledger holds what the disk holds");
            Check(Buy(ledger, "srv", "buy-1", "engineering", 300).Outcome == ResearchOutcome.Researched && Available(ledger, "srv") == 700 &&
                ledger.Research("srv", "buy-1", "engineering", 300).Status == ResearchStatus.Duplicate &&
                ledger.PendingEffects(8).Single().Id == "srv/buy-1" && ledger.Totals().Balanced, "the refused purchase is retried under its ID, once");
        }
        finally
        {
            ledger.Dispose();
        }
    }

    /// <summary>Appends what an interrupted write leaves behind: the start of a record that never completed.</summary>
    private static void Tear(string path)
    {
        using var tail = new FileStream(path, FileMode.Append, FileAccess.Write);
        tail.Write([40, 0, 0, 0, 1, 2, 3, 4, (byte)'{', (byte)'"']);
    }

    private static ResearchLedger Fresh(string directory, string name, params (string Id, string Domain)[] storages)
    {
        var ledger = ResearchLedger.Open(Path.Combine(directory, name + ".journal"), Guid.NewGuid());
        ledger.InstallCatalog(Catalog());
        foreach (var (id, domain) in storages)
            ledger.RegisterStorage(id, 1, domain);
        return ledger;
    }

    private static void Sync(ResearchLedger ledger, string to, string from, int batch = 2)
    {
        for (var round = 0; round < 20; round++)
        {
            var delta = ledger.Delta(from, ledger.Watermark(to, from), batch)!;
            var status = ledger.Replicate(to, 1, from, delta.Upto, delta.Nodes, delta.Boosts).Status;
            Check(status is ResearchStatus.Ok or ResearchStatus.Duplicate, $"replicate {from} -> {to}: {status}");
            if (!delta.More)
                return;
        }
        throw new InvalidOperationException("Research check failed: sync did not converge.");
    }

    /// <summary>§13.6: knowledge moves between peers of a domain as a union; points never move with it.</summary>
    private static void CheckSync(string directory)
    {
        using var ledger = Fresh(directory, "sync", ("a", "station"), ("b", "station"), ("c", "station"), ("x", "syndicate"));
        ledger.GrantIncome(1, [new("a", 1, Points(1000)), new("b", 1, Points(100))]);
        Buy(ledger, "a", "op-a1", "engineering", 300);
        Buy(ledger, "a", "op-a2", "advanced", 500);
        ledger.Boost("a", 1, "secret", "scan-1", new Dictionary<string, long> { [General] = 150 });
        Buy(ledger, "b", "op-b1", "cheap", 100);
        var before = ledger.Totals();
        Sync(ledger, "b", "a");
        var b = ledger.Storage("b")!;
        Check(b.Unlocked.Contains("engineering") && b.Unlocked.Contains("advanced") && b.Unlocked.Contains("cheap"),
            "replication is a union of independent knowledge");
        Check(ledger.Price("b", "secret")![General] == 50 * ResearchLedger.Milli, "a boost travels with knowledge");
        Check(Available(ledger, "a") == 200 && Available(ledger, "b") == 0 && ledger.Totals().Minted[General] == before.Minted[General],
            "knowledge sync moves no points");
        var replay = ledger.Delta("a", 0, 100)!;
        Check(ledger.Replicate("b", 1, "a", replay.Upto, replay.Nodes, replay.Boosts).Status == ResearchStatus.Duplicate,
            "replaying a batch changes nothing");
        Sync(ledger, "c", "b");
        Sync(ledger, "a", "c");
        Check(ledger.Storage("a")!.Unlocked.Contains("cheap") && ledger.Storage("c")!.Unlocked.Count == ledger.Storage("b")!.Unlocked.Count,
            "a cycle A -> B -> C -> A converges");
        var revision = ledger.Storage("a")!.Revision;
        Sync(ledger, "a", "c");
        Check(ledger.Storage("a")!.Revision == revision, "a converged cycle adds nothing");
        var delta = ledger.Delta("a", 0, 100)!;
        Check(ledger.Replicate("x", 1, "a", delta.Upto, delta.Nodes, delta.Boosts).Status == ResearchStatus.Forbidden,
            "another domain is not a trusted peer");
        Check(ledger.Replicate("b", 1, "a", 1, ["nope"], new Dictionary<string, IReadOnlyDictionary<string, long>>()).Status ==
            ResearchStatus.UnknownNode, "unknown content is refused");
        Check(ledger.Totals().Balanced, "sync keeps the ledger balanced");
    }

    /// <summary>§13.7: reserve, deliver, commit; cancel or expire before commit; destruction at every step.</summary>
    private static void CheckTransfers(string directory)
    {
        var path = Path.Combine(directory, "transfers.journal");
        var round = Guid.NewGuid();
        using (var ledger = ResearchLedger.Open(path, round))
        {
            ledger.InstallCatalog(Catalog());
            foreach (var id in new[] { "a", "b", "c", "d" })
                ledger.RegisterStorage(id, 1, "station");
            ledger.RegisterStorage("x", 1, "syndicate");
            ledger.GrantIncome(1, [new("a", 1, Points(1000)), new("b", 1, Points(200)), new("c", 1, Points(500)), new("d", 1, Points(400))]);

            // The design's example: A=1000, B=200, reserve 300, commit; ten repeats change nothing.
            var reserved = ledger.Reserve("t1", "a", 1, "b", Points(300), 1_000);
            Check(reserved.Status == ResearchStatus.Ok && Available(ledger, "a") == 700 && Available(ledger, "b") == 200 &&
                ledger.Totals().Escrow[General] == 300 * ResearchLedger.Milli, "reserve moves points to escrow only");
            Check(ledger.Reserve("t1", "a", 1, "b", Points(300), 1_000).Status == ResearchStatus.Duplicate, "a repeated reserve returns it");
            Check(ledger.Reserve("t1", "a", 1, "b", Points(301), 1_000).Status == ResearchStatus.Conflict, "other content under a transfer ID");
            Check(ledger.CommitTransfer("t1", "c", 1, 10).Status == ResearchStatus.Conflict, "only the named destination commits");
            for (var i = 0; i < 10; i++)
                ledger.CommitTransfer("t1", "b", 1, 10);
            Check(Available(ledger, "a") == 700 && Available(ledger, "b") == 500 && ledger.Totals().Escrow[General] == 0 &&
                ledger.Transfer("t1")!.State == TransferState.Committed, "commit once, then a lost ACK repeated ten times");

            Check(ledger.Reserve("t2", "a", 1, "b", Points(800), 1_000).Status == ResearchStatus.InsufficientPoints, "no overdraft");
            Check(ledger.Reserve("t3", "a", 1, "x", Points(10), 1_000).Status == ResearchStatus.Forbidden, "no transfer across domains");

            // Expiry before commit refunds a living source; a late commit gets nothing.
            ledger.Reserve("t4", "a", 1, "b", Points(100), 1_000);
            Check(ledger.ExpireTransfers(2_000) == 1 && Available(ledger, "a") == 700 && ledger.Transfer("t4")!.State == TransferState.Expired,
                "expiry before commit refunds the source");
            Check(ledger.CommitTransfer("t4", "b", 1, 2_100).Status == ResearchStatus.TransferClosed && Available(ledger, "b") == 500,
                "an expired transfer cannot be committed");
            ledger.Reserve("t5", "a", 1, "b", Points(100), 1_000);
            Check(ledger.CommitTransfer("t5", "b", 1, 1_500).Status == ResearchStatus.TransferClosed && Available(ledger, "a") == 700,
                "a commit past the deadline settles the transfer instead");

            // The destination is destroyed before commit: it takes nothing; expiry refunds the source.
            ledger.Reserve("t6", "c", 1, "d", Points(200), 1_000);
            ledger.DestroyStorage("d", 1);
            Check(ledger.CommitTransfer("t6", "d", 1, 10).Status == ResearchStatus.StorageDestroyed, "a destroyed destination takes nothing");
            ledger.ExpireTransfers(5_000);
            Check(Available(ledger, "c") == 500 && ledger.Transfer("t6")!.State == TransferState.Expired, "the source gets its escrow back");

            // The source is destroyed before commit: a cancel cannot refund it, the sum is lost.
            ledger.Reserve("t7", "c", 1, "b", Points(150), 10_000);
            ledger.DestroyStorage("c", 1);
            Check(ledger.CancelTransfer("t7").View!.State == TransferState.Lost, "the escrow of a destroyed source is lost");
            Check(ledger.CancelTransfer("t7").Status == ResearchStatus.Duplicate, "a settled transfer stays settled");

            // A reserved transfer survives a restart and commits once afterwards.
            ledger.Reserve("t8", "a", 1, "b", Points(50), 10_000);
            Check(ledger.Totals().Balanced, "transfers keep the ledger balanced");
        }
        using (var reopened = ResearchLedger.Open(path, round))
        {
            reopened.InstallCatalog(Catalog());
            Check(reopened.Transfer("t8")!.State == TransferState.Reserved && reopened.Totals().Escrow[General] == 50 * ResearchLedger.Milli,
                "a reserved transfer survives a restart");
            Check(reopened.CommitTransfer("t8", "b", 1, 100).Status == ResearchStatus.Ok &&
                reopened.CommitTransfer("t8", "b", 1, 100).Status == ResearchStatus.Duplicate && Available(reopened, "b") == 550,
                "the retried delivery commits once after the restart");
            var totals = reopened.Totals();
            Check(totals.Balanced && totals.Lost[General] == (400 + 350 + 150) * ResearchLedger.Milli,
                "lost = destroyed wallets of d and c + the escrow of c");
        }
    }

    /// <summary>§13.8: a disk carries knowledge only; restore never brings points back.</summary>
    private static void CheckDisks(string directory)
    {
        using var ledger = Fresh(directory, "disks", ("a", "station"), ("b", "station"));
        ledger.GrantIncome(1, [new("a", 1, Points(1000))]);
        Buy(ledger, "a", "op-1", "engineering", 300);
        ledger.Boost("a", 1, "secret", "scan-1", new Dictionary<string, long> { [General] = 150 });
        var written = ledger.Backup("disk-1", "a", 1);
        Check(written.Status == ResearchStatus.Ok && written.View!.Nodes == 2 && written.View.Version == 1, "backup writes knowledge");
        Check(ledger.Backup("disk-1", "a", 1).Status == ResearchStatus.Duplicate, "the same content is not written again");
        Buy(ledger, "a", "op-2", "advanced", 500);
        Check(ledger.Restore("disk-1", "a", 1) == (ResearchStatus.Ok, 0) && Available(ledger, "a") == 200 &&
            ledger.Storage("a")!.Unlocked.Contains("advanced"), "backup, spend, restore: no points return, newer knowledge stays");
        var minted = ledger.Totals().Minted[General];
        var known = ledger.Storage("b")!.Knowledge;
        Check(ledger.Restore("disk-1", "b", 1) == (ResearchStatus.Ok, 1) && ledger.Storage("b")!.Unlocked.Contains("engineering") &&
            ledger.Price("b", "secret")![General] == 50 * ResearchLedger.Milli && Available(ledger, "b") == 0, "a disk teaches another server");
        // The world learns what a server knows from the server's own log, not from the answer to the
        // console that asked: the restored node is there whether that answer arrived or not.
        var learned = ledger.Delta("b", known, 64)!;
        Check(learned.Nodes.SequenceEqual(["engineering"]) && !learned.More && learned.Upto == ledger.Storage("b")!.Knowledge &&
            learned.Upto > known, "a restored node is a record of the server's knowledge log");
        Check(ledger.Delta("b", learned.Upto, 64) is { Nodes.Count: 0, More: false } rest && rest.Upto == learned.Upto,
            "a log read to its end has nothing more");
        Check(ledger.Restore("disk-1", "b", 1).Status == ResearchStatus.Duplicate && ledger.Totals().Minted[General] == minted &&
            ledger.Storage("b")!.Knowledge == learned.Upto, "a repeated restore creates no reward and no record");
        ledger.DestroyStorage("a", 1);
        ledger.RegisterStorage("c", 1, "station");
        Check(ledger.Restore("disk-1", "c", 1).Added == 1 && !ledger.Storage("c")!.Unlocked.Contains("advanced"),
            "after destruction a disk restores only its recorded revision");
        Check(ledger.Restore("disk-9", "c", 1).Status == ResearchStatus.UnknownDisk, "an empty disk restores nothing");
        Check(ledger.Totals().Balanced, "disks keep the ledger balanced");
    }

    /// <summary>
    /// Random operations; then the journal is cut at random offsets as a crash would leave it. The
    /// recovered ledger always equals the state after the last whole record and stays balanced.
    /// </summary>
    private static void CheckCrashInjection(string directory)
    {
        var random = new Random(5150);
        var path = Path.Combine(directory, "crash.journal");
        var round = Guid.NewGuid();
        var boundaries = new List<(long Length, string Fingerprint)>();
        var operations = new List<(string Storage, string Op)>();
        using (var ledger = ResearchLedger.Open(path, round))
        {
            ledger.InstallCatalog(Catalog());
            string[] storages = ["s1", "s2", "s3"];
            foreach (var storage in storages)
                ledger.RegisterStorage(storage, 1, "station");
            boundaries.Add((new FileInfo(path).Length, Fingerprint(ledger, operations)));
            string[] nodes = ["engineering", "advanced", "cheap", "secret"];
            for (var step = 0; step < 300; step++)
            {
                var storage = storages[random.Next(storages.Length)];
                var other = storages[random.Next(storages.Length)];
                switch (random.Next(18))
                {
                    case >= 16:
                        // DM settles the oldest obligation, sometimes repeating a settlement that is already recorded.
                        if (ledger.PendingEffects(1).FirstOrDefault() is { } owed)
                            ledger.SettleEffects([(owed.Id, random.Next(3) == 0 ? "void" : "paid"), ("s1/op-" + random.Next(150), "paid")]);
                        break;
                    case 10:
                        if (other != storage && ledger.Delta(other, ledger.Watermark(storage, other), 2) is { } delta)
                            ledger.Replicate(storage, 1, other, delta.Upto, delta.Nodes, delta.Boosts);
                        break;
                    case 11:
                        if (other != storage)
                            ledger.Reserve("t-" + random.Next(60), storage, 1, other, Points(random.Next(1, 200)), random.Next(1, 100));
                        break;
                    case 12:
                        var transfer = ledger.Transfers().Where(t => t.State == TransferState.Reserved).Select(t => t.Transfer).FirstOrDefault();
                        if (transfer is not null)
                            ledger.CommitTransfer(transfer, ledger.Transfer(transfer)!.Destination, 1, random.Next(0, 100));
                        break;
                    case 13:
                        ledger.ExpireTransfers(random.Next(0, 120));
                        break;
                    case 14:
                        ledger.Backup("disk-" + random.Next(4), storage, 1);
                        break;
                    case 15:
                        ledger.Restore("disk-" + random.Next(4), storage, 1);
                        break;
                    case < 4:
                        ledger.GrantIncome(random.Next(60), [new(storage, 1, Points(random.Next(1, 200)))]);
                        break;
                    case < 5:
                        ledger.Grant(storage, 1, "event-" + random.Next(40), "analysis", Points(random.Next(1, 300)));
                        break;
                    case < 6:
                        ledger.Boost(storage, 1, nodes[random.Next(nodes.Length)], "evidence-" + random.Next(20),
                            new Dictionary<string, long> { [General] = random.Next(0, 400) });
                        break;
                    case < 9:
                        var node = nodes[random.Next(nodes.Length)];
                        var op = "op-" + random.Next(150);
                        var price = ledger.Price(storage, node)?.GetValueOrDefault(General) / ResearchLedger.Milli ?? 0;
                        ledger.Research(storage, op, node, random.Next(5) == 0 ? price + 1 : price);
                        operations.Add((storage, op));
                        break;
                    default:
                        if (random.Next(8) == 0)
                            ledger.DestroyStorage(storage, 1);
                        break;
                }
                Check(ledger.Totals().Balanced, "invariant holds after every operation");
                boundaries.Add((new FileInfo(path).Length, Fingerprint(ledger, operations)));
            }
        }
        var full = File.ReadAllBytes(path);
        for (var trial = 0; trial < 120; trial++)
        {
            var cut = trial == 0 ? full.Length : (int)random.NextInt64(boundaries[0].Length, full.Length);
            var copy = Path.Combine(directory, $"crash-{trial}.journal");
            File.WriteAllBytes(copy, full[..cut]);
            var expected = boundaries.Last(boundary => boundary.Length <= cut);
            using (var recovered = ResearchLedger.Open(copy, round))
            {
                Check(recovered.Totals().Balanced, "recovered ledger stays balanced");
                Check(Fingerprint(recovered, operations) == expected.Fingerprint,
                    $"a crash at byte {cut} recovers the last acknowledged state");
                Check(cut == full.Length ? recovered.TruncatedBytes == 0 : recovered.TruncatedBytes == cut - expected.Length,
                    "only the torn tail is cut off");
            }
            Check(new FileInfo(copy).Length == expected.Length, "an accepted journal is repaired when it is opened");
            File.Delete(copy);
        }
    }

    /// <summary>
    /// Damage in the middle of the journal cannot be the tail an interrupted append leaves: the records
    /// behind it were flushed before they were acknowledged. The journal is refused whole and left byte
    /// for byte as it was, so confirmed research records are never cut away silently, and restoring the
    /// file makes the round usable again.
    /// </summary>
    private static void CheckJournalDamage(string directory)
    {
        var round = Guid.NewGuid();
        var path = Path.Combine(directory, "damage.journal");
        using (var ledger = ResearchLedger.Open(path, round))
        {
            ledger.InstallCatalog(Catalog());
            ledger.RegisterStorage("srv", 1, "station");
            ledger.GrantIncome(1, [new("srv", 1, Points(1000))]);
            Buy(ledger, "srv", "op-1", "cheap", 100);
            Buy(ledger, "srv", "op-2", "engineering", 300);
        }
        var full = File.ReadAllBytes(path);
        var offsets = RecordOffsets(full);
        Check(offsets.Count >= 6, "the journal holds the records of the round");

        // A whole record whose payload changed after it was flushed: its length is intact, its checksum is not.
        var damaged = (byte[])full.Clone();
        damaged[offsets[3] + 10] ^= 0xFF;
        File.WriteAllBytes(path, damaged);
        var before = File.ReadAllBytes(path);
        var exception = Damage(path, round);
        Check(exception is { Reason: "checksum", WholeRecords: 3 } && exception.Offset == offsets[3] &&
            exception.Length == before.Length, $"damage after three whole records is named: {exception.Reason} at {exception.Offset}");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "a journal damaged in the middle is left byte for byte as it was");

        // A whole record header that declares a length no append ever wrote.
        damaged = (byte[])full.Clone();
        damaged[offsets[1]] = damaged[offsets[1] + 1] = damaged[offsets[1] + 2] = damaged[offsets[1] + 3] = 0;
        File.WriteAllBytes(path, damaged);
        before = File.ReadAllBytes(path);
        exception = Damage(path, round);
        Check(exception is { Reason: "invalid_length", WholeRecords: 1 } && exception.Offset == offsets[1],
            $"an impossible record length is damage, not a tail: {exception.Reason} at {exception.Offset}");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "an impossible length does not shorten the journal");

        // Damage of the first record: the old behaviour wrote a new round over such a file and lost every
        // acknowledged record of the round. It must be refused instead.
        damaged = (byte[])full.Clone();
        damaged[offsets[0] + 9] ^= 0xFF;
        File.WriteAllBytes(path, damaged);
        before = File.ReadAllBytes(path);
        exception = Damage(path, round);
        Check(exception is { WholeRecords: 0 } && exception.Offset == offsets[0],
            $"damage before any whole record is refused too: {exception.Reason} at {exception.Offset}");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "damage of the first record does not start a fresh journal");

        // An operator restoring the journal ends the refusal: the same round comes back with its records.
        File.WriteAllBytes(path, full);
        using (var restored = ResearchLedger.Open(path, round))
            Check(restored.RecoveredEntries == offsets.Count && Available(restored, "srv") == 600 && restored.Totals().Balanced,
                "a restored journal opens with its confirmed records");
    }

    /// <summary>Byte offset of every whole record's length field, the header excluded.</summary>
    private static List<long> RecordOffsets(byte[] journal)
    {
        var offsets = new List<long>();
        var offset = 8L;
        while (offset + 8 <= journal.Length)
        {
            offsets.Add(offset);
            var size = BitConverter.ToInt32(journal, (int)offset);
            if (size is <= 0 or > ResearchJournal.MaxRecordBytes || offset + 8 + size > journal.Length)
                break;
            offset += 8 + size;
        }
        return offsets;
    }

    private static ResearchJournalCorruptException Damage(string path, Guid round)
    {
        try
        {
            using var journal = new ResearchJournal(path);
        }
        catch (ResearchJournalCorruptException exception)
        {
            Check(exception.WholeRecords >= 0, "damage is reported with its position");
        }
        try
        {
            using var ledger = ResearchLedger.Open(path, round);
        }
        catch (ResearchJournalCorruptException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Research check failed: a damaged journal was accepted.");
    }

    private static string Fingerprint(ResearchLedger ledger, IEnumerable<(string Storage, string Op)> operations)
    {
        var storages = string.Join(";", ledger.Storages().Select(s =>
            $"{s.StorageId}:{s.Destroyed}:{s.Revision}:{string.Join(",", s.Unlocked)}:{s.AvailableMilli.GetValueOrDefault(General)}"));
        var totals = ledger.Totals();
        var transfers = string.Join(",", ledger.Transfers().Select(t => $"{t.Transfer}:{t.State}"));
        var effects = ledger.EffectTotals();
        var owed = string.Join(",", ledger.PendingEffects(int.MaxValue).Select(effect => $"{effect.Id}:{effect.Node}:{effect.PriceMilli}")) +
            "/" + string.Join(",", effects.Settled.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return $"{storages}|{totals.Minted.GetValueOrDefault(General)}/{totals.Spent.GetValueOrDefault(General)}/" +
            $"{totals.Lost.GetValueOrDefault(General)}/{totals.Escrow.GetValueOrDefault(General)}|{ledger.OperationCount}|{transfers}|{owed}";
    }

    private static ResearchCommandResult Buy(ResearchLedger ledger, string storage, string op, string node, long quote,
        long? expectedRevision = null) => ledger.Research(storage, op, node, quote, expectedRevision).Result;

    private static long Available(ResearchLedger ledger, string storage) =>
        ledger.Storage(storage)!.AvailableMilli.GetValueOrDefault(General) / ResearchLedger.Milli;

    private static void Reject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Research check failed: " + message + " was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Research check failed: " + message);
    }
}

// Wire contracts for research. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterResearch(SchemaRegistry registry)
    {
        // research.server (P5): the Host runs the server's storage. Points on the wire are whole points;
        // the ledger keeps milli-points. A purchase quotes the price the user saw, so a changed price
        // answers quote_changed instead of spending an unexpected sum.
        registry.Register(new SchemaDefinition("research.request", 1, 0,
        [
            new SchemaField("node", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("quote", SchemaFieldType.Integer, true, 0, 1_000_000_000),
            new SchemaField("revision", SchemaFieldType.Integer, false, 0)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["node", "quote", "revision"] }, MaxPayloadBytes: 256));
        registry.Register(new SchemaDefinition("research.result", 1, 0,
        [
            new SchemaField("outcome", SchemaFieldType.Enum, true, Values: ResearchOutcomes),
            new SchemaField("node", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("revision", SchemaFieldType.Integer, true, 0),
            new SchemaField("price", SchemaFieldType.Integer, true, 0),
            new SchemaField("available", SchemaFieldType.Integer, true, 0)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["outcome", "node", "revision", "price", "available"] },
            MaxPayloadBytes: 256));
        registry.Register(new SchemaDefinition("research.status_request", 1, 0,
            [new SchemaField("reason", SchemaFieldType.Enum, false, Values: ["open", "refresh"])],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["reason"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("research.status", 1, 0,
        [
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["online", "destroyed", "catalog_unavailable"]),
            new SchemaField("revision", SchemaFieldType.Integer, true, 0),
            new SchemaField("available", SchemaFieldType.Integer, true, 0),
            new SchemaField("unlocked", SchemaFieldType.Integer, true, 0),
            new SchemaField("catalog", SchemaFieldType.Text, true, MaxLength: 16)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["state", "revision", "available", "unlocked", "catalog"] },
            MaxPayloadBytes: 256));
        registry.RegisterCommand("research.server", "research", "research.request", 1, "research.result", 1);
        registry.RegisterCommand("research.server", "status", "research.status_request", 1, "research.status", 1);

        // P6 sync (§13.6): a peer pulls the provider's knowledge log after its watermark. Node IDs and
        // boosts of the primary point type travel as bounded text: "a,b,c" and "node=amount;node=amount".
        registry.Register(new SchemaDefinition("research.sync_request", 1, 0,
            [new SchemaField("since", SchemaFieldType.Integer, true, 0)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["since"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("research.sync_delta", 1, 0,
        [
            new SchemaField("nodes", SchemaFieldType.Text, true, MaxLength: 4000),
            new SchemaField("boosts", SchemaFieldType.Text, true, MaxLength: 4000),
            new SchemaField("upto", SchemaFieldType.Integer, true, 0),
            new SchemaField("more", SchemaFieldType.Boolean, true),
            new SchemaField("revision", SchemaFieldType.Integer, true, 0)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["nodes", "boosts", "upto", "more", "revision"] },
            MaxPayloadBytes: 8192));
        registry.RegisterCommand("research.server", "sync", "research.sync_request", 1, "research.sync_delta", 1);

        // P6 transfers (§13.7): transfer_out reserves on the source; the authorization then travels from the
        // source to the destination as transfer_in, which commits. Amounts are whole points.
        string[] transferStates = ["reserved", "committed", "cancelled", "expired", "lost", "refused"];
        registry.Register(new SchemaDefinition("research.transfer_request", 1, 0,
        [
            new SchemaField("transfer", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("to", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("amount", SchemaFieldType.Integer, true, 1, 1_000_000_000)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["transfer", "to", "amount"] }, MaxPayloadBytes: 256));
        registry.Register(new SchemaDefinition("research.transfer_authorization", 1, 0,
        [
            new SchemaField("transfer", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("from", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("amount", SchemaFieldType.Integer, true, 1, 1_000_000_000)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["transfer", "from", "amount"] }, MaxPayloadBytes: 256));
        registry.Register(new SchemaDefinition("research.transfer_state", 1, 0,
        [
            new SchemaField("transfer", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: transferStates),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("amount", SchemaFieldType.Integer, true, 0),
            new SchemaField("available", SchemaFieldType.Integer, true, 0)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["transfer", "state", "reason", "amount", "available"] },
            MaxPayloadBytes: 256));
        registry.RegisterCommand("research.server", "transfer_out", "research.transfer_request", 1, "research.transfer_state", 1);
        registry.RegisterCommand("research.server", "transfer_in", "research.transfer_authorization", 1, "research.transfer_state", 1);

        // P6 disks (§13.8): backup writes the server's knowledge to a disk manifest, restore imports one.
        registry.Register(new SchemaDefinition("research.disk_request", 1, 0,
            [new SchemaField("disk", SchemaFieldType.Text, true, MaxLength: 64)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["disk"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("research.disk_state", 1, 0,
        [
            new SchemaField("disk", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["written", "restored", "unchanged", "refused"]),
            new SchemaField("nodes", SchemaFieldType.Integer, true, 0),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["disk", "state", "nodes", "reason"] }, MaxPayloadBytes: 256));
        registry.RegisterCommand("research.server", "backup", "research.disk_request", 1, "research.disk_state", 1);
        registry.RegisterCommand("research.server", "restore", "research.disk_request", 1, "research.disk_state", 1);

    }
}

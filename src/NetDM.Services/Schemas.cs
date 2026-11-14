using System.Buffers;
using System.Text.Json;
using NetDM.Services.Shell;

namespace NetDM.Services;

public enum SchemaFieldType : byte { Integer, Number, Text, Enum, Boolean, MultilineText }

/// <summary>
/// One declared payload field. Values outside the declared range, type or length
/// are rejected; there is no coercion and no implicit unit conversion.
/// </summary>
public sealed record SchemaField(
    string Name, SchemaFieldType Type, bool Required,
    double Min = -9_007_199_254_740_991, double Max = 9_007_199_254_740_991,
    int MaxLength = 0, IReadOnlyList<string>? Values = null, int SinceMinor = 0);

/// <summary>
/// A versioned message contract. Minor versions only add optional fields. A
/// projection is a named subset of fields that a recipient may accept.
/// </summary>
public sealed record SchemaDefinition(
    string Id, int Major, int Minor, IReadOnlyList<SchemaField> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Projections, int MaxPayloadBytes = 1024)
{
    public SchemaField? Field(string name)
    {
        foreach (var field in Fields)
            if (field.Name == name)
                return field;
        return null;
    }
}

/// <summary>A payload that passed validation, in schema field order.</summary>
public sealed class CanonicalPayload
{
    internal CanonicalPayload(SchemaDefinition schema, int minor, IReadOnlyDictionary<string, JsonElement> values, string digest)
    {
        Schema = schema;
        Minor = minor;
        Values = values;
        Digest = digest;
    }

    public SchemaDefinition Schema { get; }
    public int Minor { get; }
    public IReadOnlyDictionary<string, JsonElement> Values { get; }
    /// <summary>Hash of the canonical form; equal content always has an equal digest.</summary>
    public string Digest { get; }
}

/// <summary>
/// A command of a service type: validated arguments in, validated result out.
/// Host-handled commands are answered by the Host on the provider's behalf after
/// the request physically reaches the provider; DM never sees them.
/// </summary>
public sealed record CommandDefinition(string ServiceType, string Name, SchemaDefinition Arguments,
    SchemaDefinition Result, bool HostHandled = false);

public sealed class SchemaRegistry
{
    public const int AbsoluteMaxPayloadBytes = 8 * 1024;
    public const string DescribeCommand = "directory.describe";
    /// <summary>Outcomes of research.server/research, in the wire spelling.</summary>
    public static readonly string[] ResearchOutcomes =
    [
        "researched", "already_researched", "prerequisites_missing", "quote_changed", "insufficient_points",
        "unknown_node", "stale_revision", "storage_unavailable", "catalog_unavailable"
    ];
    /// <summary>A text field's own cap; the payload cap (8 KiB at most) still bounds the message.
    /// A research sync batch carries up to 4000 characters of node IDs.</summary>
    private const int MaxTextLength = 4096;
    private readonly Dictionary<(string Id, int Major), SchemaDefinition> _schemas = new();
    private readonly Dictionary<(string ServiceType, string Name), CommandDefinition> _commands = new();
    private readonly Dictionary<string, List<string>> _commandNames = new(StringComparer.Ordinal);

    public static SchemaRegistry CreateDefault()
    {
        var registry = new SchemaRegistry();
        registry.Register(new SchemaDefinition("atmos.sample", 1, 0,
        [
            new SchemaField("pressureKPa", SchemaFieldType.Number, true, 0, 1_000_000),
            new SchemaField("temperatureK", SchemaFieldType.Number, true, 0, 1_000_000),
            new SchemaField("sampleTick", SchemaFieldType.Integer, true, 0),
            new SchemaField("quality", SchemaFieldType.Enum, true, Values: ["current", "stale", "fault"])
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["pressureKPa", "temperatureK", "sampleTick", "quality"],
            ["panel.v1"] = ["pressureKPa", "temperatureK", "quality"]
        }, MaxPayloadBytes: 512));

        // atmos.telemetry/sample: a subscriber asks the sensor for its latest sample; the answer is a
        // result over the network, so a late or reconnected display gets a snapshot without waiting.
        registry.Register(new SchemaDefinition("atmos.sample_request", 1, 0,
            [new SchemaField("reason", SchemaFieldType.Enum, false, Values: ["subscribe", "refresh"])],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["reason"] }, MaxPayloadBytes: 64));
        registry.RegisterCommand("atmos.telemetry", "sample", "atmos.sample_request", 1, "atmos.sample", 1);

        // machine.control: an idempotent set_state command, never a toggle.
        registry.Register(new SchemaDefinition("machine.power_state", 1, 0,
        [
            new SchemaField("on", SchemaFieldType.Boolean, true),
            new SchemaField("changed", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["on", "changed"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("machine.set_power", 1, 0,
            [new SchemaField("on", SchemaFieldType.Boolean, true)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["on"] }, MaxPayloadBytes: 64));
        registry.RegisterCommand("machine.control", "set_power", "machine.set_power", 1, "machine.power_state", 1);

        // machine.door (P9): a door control (a button's blast door or airlock controller) sets the state it
        // wants on each door it controls; absent fields stay as they are. The door answers its state after
        // its own guards (power, bolts, wires). "press" ties the doors of one button press together.
        registry.Register(new SchemaDefinition("machine.door_set", 1, 0,
        [
            new SchemaField("press", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, false, Values: ["open", "closed"]),
            new SchemaField("bolted", SchemaFieldType.Boolean, false),
            new SchemaField("shocked", SchemaFieldType.Boolean, false),
            new SchemaField("id_scan", SchemaFieldType.Boolean, false),
            new SchemaField("safety", SchemaFieldType.Boolean, false)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["press", "state", "bolted", "shocked", "id_scan", "safety"] },
            MaxPayloadBytes: 256));
        registry.Register(new SchemaDefinition("machine.door_state", 1, 0,
        [
            new SchemaField("press", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["open", "closed"]),
            new SchemaField("bolted", SchemaFieldType.Boolean, false),
            new SchemaField("changed", SchemaFieldType.Boolean, true),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["press", "state", "bolted", "changed", "reason"] },
            MaxPayloadBytes: 256));
        registry.RegisterCommand("machine.door", "set_state", "machine.door_set", 1, "machine.door_state", 1);

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

        // pda.messenger (P7): an addressed message to one messenger endpoint. The text is the sanitized
        // presentation text; a photo travels only as a flag (V1 has no attachment lifecycle). The recipient
        // shows a message ID once; its receipt is the result leg back to the sender. "from" is the
        // sender's messenger instance when a message server relays it, so a reply finds the sender.
        registry.Register(new SchemaDefinition("pda.message", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("text", SchemaFieldType.Text, true, MaxLength: 4000),
            new SchemaField("sender", SchemaFieldType.Text, true, MaxLength: 128),
            new SchemaField("from", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("automated", SchemaFieldType.Boolean, false),
            new SchemaField("photo", SchemaFieldType.Boolean, false)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "text", "sender", "from", "automated", "photo"] },
            MaxPayloadBytes: 8192));
        registry.Register(new SchemaDefinition("pda.receipt", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["shown", "recorded", "duplicate", "blocked", "disabled"]),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "state", "reason"] }, MaxPayloadBytes: 256));
        registry.RegisterCommand("pda.messenger", "deliver", "pda.message", 1, "pda.receipt", 1);

        // Authoritative messenger (P7): the sender's PDA hands a message to a message server ("relay"),
        // the server logs it and delivers it to each recipient, and reports each recipient's outcome
        // back to the sender ("status"). "to" is a comma-separated list of messenger instances.
        registry.Register(new SchemaDefinition("pda.relay", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("text", SchemaFieldType.Text, true, MaxLength: 4000),
            new SchemaField("sender", SchemaFieldType.Text, true, MaxLength: 128),
            new SchemaField("to", SchemaFieldType.Text, true, MaxLength: 1024)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "text", "sender", "to"] }, MaxPayloadBytes: 8192));
        registry.Register(new SchemaDefinition("pda.relay_state", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["queued", "offline", "refused"]),
            new SchemaField("recipients", SchemaFieldType.Integer, false, 0),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "state", "recipients", "reason"] }, MaxPayloadBytes: 256));
        registry.RegisterCommand("pda.server", "relay", "pda.relay", 1, "pda.relay_state", 1);
        registry.Register(new SchemaDefinition("pda.delivery", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("recipient", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true,
                Values: ["delivered", "duplicate", "blocked", "disabled", "unreachable"]),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "recipient", "state", "reason"] }, MaxPayloadBytes: 256));
        registry.RegisterCommand("pda.messenger", "status", "pda.delivery", 1, "pda.receipt", 1);

        // radio.receiver (P8, uplink over the network): a speaker's radio hands a transmission to a
        // telecomms receiver. The speech itself stays in DM as a presentation context; only its ID and
        // frequency travel. The receiver answers what it did with it; broadcasting stays subspace.
        registry.Register(new SchemaDefinition("radio.uplink", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("frequency", SchemaFieldType.Integer, true, 0)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "frequency"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("radio.uplink_state", 1, 0,
        [
            new SchemaField("id", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["relayed", "ignored", "duplicate", "late", "expired"])
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["id", "state"] }, MaxPayloadBytes: 128));
        registry.RegisterCommand("radio.receiver", "uplink", "radio.uplink", 1, "radio.uplink_state", 1);

        // network.diagnostics (§7.6): a network device answers about itself and the network around it.
        // The Host runs it on the device's behalf once the request reached the device's node. Nodes are
        // named by their decimal IDs; "neighbors" lists the device's own ports as "id:state,id:state".
        registry.Register(new SchemaDefinition("network.link_request", 1, 0,
            [new SchemaField("reason", SchemaFieldType.Enum, false, Values: ["inspect", "refresh"])],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["reason"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("network.link_state", 1, 0,
        [
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["active", "unpowered", "broken"]),
            new SchemaField("wired", SchemaFieldType.Integer, true, 0, 4096),
            new SchemaField("wiredUp", SchemaFieldType.Integer, true, 0, 4096),
            new SchemaField("radio", SchemaFieldType.Boolean, true),
            new SchemaField("devices", SchemaFieldType.Integer, true, 0, 65_536),
            new SchemaField("neighbors", SchemaFieldType.Text, true, MaxLength: 512),
            new SchemaField("more", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["state", "wired", "wiredUp", "radio", "devices", "neighbors", "more"]
        }, MaxPayloadBytes: 1024));
        registry.RegisterCommand("network.diagnostics", "link_state", "network.link_request", 1, "network.link_state", 1);
        registry.Register(new SchemaDefinition("network.trace_request", 1, 0,
            [new SchemaField("node", SchemaFieldType.Text, true, MaxLength: 20)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["node"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("network.trace", 1, 0,
        [
            new SchemaField("node", SchemaFieldType.Text, true, MaxLength: 20),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["reachable", "unreachable", "unknown"]),
            new SchemaField("hops", SchemaFieldType.Integer, true, 0, 1_000_000),
            new SchemaField("radioHops", SchemaFieldType.Integer, true, 0, 1_000_000),
            new SchemaField("cost", SchemaFieldType.Integer, true, 0),
            new SchemaField("ticks", SchemaFieldType.Integer, true, 0, 1_000_000)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["node", "state", "hops", "radioHops", "cost", "ticks"] },
            MaxPayloadBytes: 256));
        registry.RegisterCommand("network.diagnostics", "trace", "network.trace_request", 1, "network.trace", 1);

        // network.query (§14.5, P10): a network device answers bounded typed queries about what the network
        // around it offers. "services" lists the services the asker may discover and the device reaches now,
        // in instance order, as "instance|serviceType|hops" items. A cursor continues the list at the same
        // directory revision; at another one the answer is snapshot_expired.
        registry.Register(new SchemaDefinition("query.services_request", 1, 0,
        [
            new SchemaField("type", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("limit", SchemaFieldType.Integer, false, 1, 16),
            new SchemaField("cursor", SchemaFieldType.Text, false, MaxLength: 160),
            // Allowlisted predicates and a projection (§14.5): an instance prefix, the most hops from the
            // device, and which parts of each item are returned. A cursor belongs to all of them.
            new SchemaField("prefix", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("maxHops", SchemaFieldType.Integer, false, 0, 64),
            new SchemaField("projection", SchemaFieldType.Enum, false, Values: ["full", "instance", "instance_type"])
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["type", "limit", "cursor", "prefix", "maxHops", "projection"] }, MaxPayloadBytes: 512));
        registry.Register(new SchemaDefinition("query.page", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "partial", "empty", "snapshot_expired", "invalid_cursor", "budget_exhausted"]),
            new SchemaField("revision", SchemaFieldType.Integer, true, 0),
            new SchemaField("count", SchemaFieldType.Integer, true, 0, 16),
            new SchemaField("items", SchemaFieldType.Text, true, MaxLength: 2400),
            new SchemaField("cursor", SchemaFieldType.Text, false, MaxLength: 160)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "revision", "count", "items", "cursor"] },
            MaxPayloadBytes: 4096));
        registry.RegisterCommand("network.query", "services", "query.services_request", 1, "query.page", 1);
        registry.Register(new SchemaDefinition("query.instance_request", 1, 0,
            [new SchemaField("instance", SchemaFieldType.Text, true, MaxLength: 64)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["instance"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("query.schema", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "empty", "invalid_request"]),
            new SchemaField("instance", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("serviceType", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("schema", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("major", SchemaFieldType.Integer, false, 1, 1000),
            new SchemaField("minor", SchemaFieldType.Integer, false, 0, 1000),
            new SchemaField("commands", SchemaFieldType.Text, false, MaxLength: 256)
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["status", "instance", "serviceType", "schema", "major", "minor", "commands"]
        }, MaxPayloadBytes: 640));
        registry.Register(new SchemaDefinition("query.state", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "empty", "invalid_request"]),
            new SchemaField("instance", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("publication", SchemaFieldType.Enum, false, Values: ["idle", "published"]),
            new SchemaField("sequence", SchemaFieldType.Text, false, MaxLength: 19)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "instance", "publication", "sequence"] }, MaxPayloadBytes: 256));
        registry.RegisterCommand("network.query", "schema", "query.instance_request", 1, "query.schema", 1);
        registry.RegisterCommand("network.query", "state", "query.instance_request", 1, "query.state", 1);
        // "route": the path a packet from the device to a service the asker may discover takes now, as
        // "node:link" items from the device (link start, cable or radio), cut after 32 nodes ("partial").
        // A service the asker may not discover is "empty", one it may but the device does not reach is
        // "unreachable".
        registry.Register(new SchemaDefinition("query.route", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "partial", "unreachable", "empty", "invalid_request"]),
            new SchemaField("instance", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("hops", SchemaFieldType.Integer, false, 0, 1_000_000),
            new SchemaField("radioHops", SchemaFieldType.Integer, false, 0, 1_000_000),
            new SchemaField("path", SchemaFieldType.Text, false, MaxLength: 1024)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "instance", "hops", "radioHops", "path"] }, MaxPayloadBytes: 1536));
        registry.RegisterCommand("network.query", "route", "query.instance_request", 1, "query.route", 1);
        // "jobs": the asker's own latest commands, newest first, as "operation|instance|command|outcome|result"
        // items. Only the asker's operations of its current endpoint generation are listed.
        registry.Register(new SchemaDefinition("query.jobs_request", 1, 0,
            [new SchemaField("limit", SchemaFieldType.Integer, false, 1, 16)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["limit"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("query.jobs", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "empty"]),
            new SchemaField("count", SchemaFieldType.Integer, true, 0, 16),
            new SchemaField("items", SchemaFieldType.Text, true, MaxLength: 2400)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "count", "items"] }, MaxPayloadBytes: 3072));
        registry.RegisterCommand("network.query", "jobs", "query.jobs_request", 1, "query.jobs", 1);
        // WatchQuery: the published state of a network.query instance. It changes when the set of services
        // the device reaches changes; a watcher then asks "services" again.
        registry.Register(new SchemaDefinition("query.directory", 1, 0,
        [
            new SchemaField("services", SchemaFieldType.Integer, true, 0, 65_536),
            new SchemaField("digest", SchemaFieldType.Text, true, MaxLength: 16),
            new SchemaField("partial", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["services", "digest", "partial"] }, MaxPayloadBytes: 128));

        // network.shell (§14.6, P11): a device runs one line of the network shell for whoever reached it.
        // "rows" holds the records as "name=value,name=value;…". A line that did not run answers its status,
        // the reason and the position in the line.
        registry.Register(new SchemaDefinition("shell.run_request", 1, 0,
            [new SchemaField("line", SchemaFieldType.Text, true, MaxLength: 1024)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["line"] }, MaxPayloadBytes: 2048));
        registry.Register(new SchemaDefinition("shell.script_request", 1, 0,
            [new SchemaField("source", SchemaFieldType.MultilineText, true, MaxLength: NetShell.MaxScriptLength)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["source"] }, MaxPayloadBytes: AbsoluteMaxPayloadBytes));
        registry.Register(new SchemaDefinition("shell.output", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values:
                ["ok", "parse_error", "unknown_command", "invalid_argument", "fuel_exhausted", "limit_exceeded", "source_failed"]),
            new SchemaField("count", SchemaFieldType.Integer, true, 0, 4096),
            new SchemaField("fuel", SchemaFieldType.Integer, true, 0, 1_000_000),
            new SchemaField("rows", SchemaFieldType.Text, true, MaxLength: 4096),
            new SchemaField("error", SchemaFieldType.Text, false, MaxLength: 200),
            new SchemaField("position", SchemaFieldType.Integer, false, 0, 4096)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "count", "fuel", "rows", "error", "position"] },
            MaxPayloadBytes: 8192));
        registry.RegisterCommand("network.shell", "run", "shell.run_request", 1, "shell.output", 1);
        registry.RegisterCommand("network.shell", "run_script", "shell.script_request", 1, "shell.output", 1);
        // Programs: a script that stays on the device after the request that started it. "on <pipeline>" as
        // its first line makes it a rule that runs when the pipeline's records change; a line "wait" holds it
        // until the commands it just sent have an outcome.
        registry.Register(new SchemaDefinition("shell.program_request", 1, 0,
        [
            new SchemaField("name", SchemaFieldType.Text, true, MaxLength: 32),
            new SchemaField("source", SchemaFieldType.MultilineText, true, MaxLength: NetShell.MaxScriptLength),
            new SchemaField("ensure", SchemaFieldType.Boolean, false)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["name", "source", "ensure"] }, MaxPayloadBytes: 4096));
        registry.Register(new SchemaDefinition("shell.stop_request", 1, 0,
            [new SchemaField("program", SchemaFieldType.Integer, true, 1, 1_000_000_000)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["program"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("shell.program", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values:
                ["started", "running", "stopped", "unknown_program", "parse_error", "unknown_command", "invalid_argument", "limit_exceeded"]),
            new SchemaField("program", SchemaFieldType.Integer, false, 0, 1_000_000_000),
            new SchemaField("error", SchemaFieldType.Text, false, MaxLength: 200),
            new SchemaField("position", SchemaFieldType.Integer, false, 0, 4096)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "program", "error", "position"] }, MaxPayloadBytes: 512));
        registry.RegisterCommand("network.shell", "start", "shell.program_request", 1, "shell.program", 1);
        registry.RegisterCommand("network.shell", "stop", "shell.stop_request", 1, "shell.program", 1);

        // Discovery is a network round trip answered by the provider's descriptor.
        registry.Register(new SchemaDefinition("directory.probe", 1, 0,
            [new SchemaField("scan", SchemaFieldType.Text, false, MaxLength: 64)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["scan"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("directory.descriptor", 1, 0,
        [
            new SchemaField("instance", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("serviceType", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("schema", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("major", SchemaFieldType.Integer, true, 1, 1000),
            new SchemaField("minor", SchemaFieldType.Integer, true, 0, 1000),
            new SchemaField("commands", SchemaFieldType.Text, false, MaxLength: 256)
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["instance", "serviceType", "schema", "major", "minor", "commands"]
        }, MaxPayloadBytes: 512));
        return registry;
    }

    public void RegisterCommand(string serviceType, string name, string argumentsSchema, int argumentsMajor,
        string resultSchema, int resultMajor, bool hostHandled = false)
    {
        var arguments = Find(argumentsSchema, argumentsMajor);
        var result = Find(resultSchema, resultMajor);
        if (!ServiceNames.IsValid(serviceType) || !ServiceNames.IsValid(name) || name == DescribeCommand ||
            arguments is null || result is null || !arguments.Projections.ContainsKey("full") ||
            !result.Projections.ContainsKey("full"))
            throw new ArgumentException("Invalid command definition.");
        if (!_commands.TryAdd((serviceType, name), new CommandDefinition(serviceType, name, arguments, result, hostHandled)))
            throw new ArgumentException($"Command '{serviceType}/{name}' is already registered.");
        if (!_commandNames.TryGetValue(serviceType, out var names))
            _commandNames[serviceType] = names = [];
        names.Add(name);
    }

    public CommandDefinition? FindCommand(string serviceType, string name) =>
        name == DescribeCommand && Find("directory.probe", 1) is { } probe && Find("directory.descriptor", 1) is { } descriptor
            ? new CommandDefinition(serviceType, DescribeCommand, probe, descriptor, HostHandled: true)
            : _commands.GetValueOrDefault((serviceType, name));

    public IReadOnlyList<string> CommandNames(string serviceType) =>
        _commandNames.TryGetValue(serviceType, out var names) ? names : [];

    public void Register(SchemaDefinition schema)
    {
        if (!ServiceNames.IsValid(schema.Id) || schema.Major < 1 || schema.Minor < 0 ||
            schema.MaxPayloadBytes is < 1 or > AbsoluteMaxPayloadBytes || schema.Fields.Count is < 1 or > 64)
            throw new ArgumentException("Invalid schema definition.", nameof(schema));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (!ServiceNames.IsValid(field.Name) || !names.Add(field.Name) || field.SinceMinor > schema.Minor ||
                (field.Required && field.SinceMinor > 0) || field.Min > field.Max ||
                (field.Type is SchemaFieldType.Text or SchemaFieldType.MultilineText && field.MaxLength is < 1 or > MaxTextLength) ||
                (field.Type == SchemaFieldType.Enum && field.Values is not { Count: > 0 }))
                throw new ArgumentException($"Invalid field '{field.Name}' in schema '{schema.Id}'.", nameof(schema));
        }
        foreach (var (name, fields) in schema.Projections)
            if (!ServiceNames.IsValid(name) || fields.Count == 0 || fields.Any(f => !names.Contains(f)))
                throw new ArgumentException($"Invalid projection '{name}' in schema '{schema.Id}'.", nameof(schema));
        if (!_schemas.TryAdd((schema.Id, schema.Major), schema))
            throw new ArgumentException($"Schema '{schema.Id}' v{schema.Major} is already registered.", nameof(schema));
    }

    public SchemaDefinition? Find(string id, int major) =>
        _schemas.TryGetValue((id, major), out var schema) ? schema : null;

    /// <summary>
    /// Validates a producer payload declared at <paramref name="minor"/>. Unknown,
    /// newer-minor, duplicate or out-of-range fields reject the whole payload.
    /// </summary>
    public ServiceStatus Validate(SchemaDefinition schema, int minor, JsonElement payload, out CanonicalPayload? canonical)
    {
        canonical = null;
        if (minor < 0 || minor > schema.Minor || payload.ValueKind != JsonValueKind.Object)
            return ServiceStatus.InvalidPayload;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            var field = schema.Field(property.Name);
            if (field is null || field.SinceMinor > minor || values.ContainsKey(property.Name) ||
                !IsValidValue(field, property.Value))
                return ServiceStatus.InvalidPayload;
            // DM encodes booleans as 0/1; the canonical form always uses true/false.
            values[property.Name] = field.Type == SchemaFieldType.Boolean && property.Value.ValueKind == JsonValueKind.Number
                ? (property.Value.GetInt32() == 1 ? True : False)
                : property.Value.Clone();
        }
        foreach (var field in schema.Fields)
            if (field.Required && !values.ContainsKey(field.Name))
                return ServiceStatus.InvalidPayload;
        var full = Write(schema, values, null);
        if (full.Length > schema.MaxPayloadBytes)
            return ServiceStatus.InvalidPayload;
        canonical = new CanonicalPayload(schema, minor, values,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(full)));
        return ServiceStatus.Ok;
    }

    /// <summary>Serializes the fields of one projection in schema order.</summary>
    public static byte[] Project(CanonicalPayload payload, string projection)
    {
        if (!payload.Schema.Projections.TryGetValue(projection, out var fields))
            throw new ArgumentException("Unknown projection.", nameof(projection));
        return Write(payload.Schema, payload.Values, new HashSet<string>(fields, StringComparer.Ordinal));
    }

    private static byte[] Write(SchemaDefinition schema, IReadOnlyDictionary<string, JsonElement> values,
        HashSet<string>? include)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in schema.Fields)
            {
                if ((include is not null && !include.Contains(field.Name)) || !values.TryGetValue(field.Name, out var value))
                    continue;
                writer.WritePropertyName(field.Name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsValidValue(SchemaField field, JsonElement value) => field.Type switch
    {
        SchemaFieldType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number) && Math.Floor(number) == number && number >= field.Min && number <= field.Max,
        SchemaFieldType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number) && number >= field.Min && number <= field.Max,
        SchemaFieldType.Text => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            text.Length <= field.MaxLength && !text.Any(char.IsControl),
        SchemaFieldType.MultilineText => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            text.Length <= field.MaxLength && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')),
        SchemaFieldType.Enum => value.ValueKind == JsonValueKind.String && field.Values!.Contains(value.GetString()!),
        SchemaFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False ||
            (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var flag) && flag is 0 or 1),
        _ => false
    };

    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();
    private static readonly JsonElement False = JsonDocument.Parse("false").RootElement.Clone();
}

/// <summary>Names of schemas, fields, services, topics and subscriptions.</summary>
public static class ServiceNames
{
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Wire code of an enum value: TransportDelivered → transport_delivered.</summary>
    public static string Code<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && char.IsLower(name[i - 1]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}

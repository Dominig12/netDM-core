// Wire contracts for diagnostics. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterDiagnostics(SchemaRegistry registry)
    {
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

    }
}

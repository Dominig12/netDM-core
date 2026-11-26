// Wire contracts for queries. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterQueries(SchemaRegistry registry)
    {
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

    }
}

// Wire contracts for directory. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterDirectory(SchemaRegistry registry)
    {
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
    }
}

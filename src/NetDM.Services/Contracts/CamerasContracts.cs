// Wire contracts for cameras. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterCameras(SchemaRegistry registry)
    {
        registry.Register(new SchemaDefinition("camera.acquire", 1, 0,
            [new SchemaField("request", SchemaFieldType.Text, true, MaxLength: 32)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["request"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("camera.access", 1, 0,
        [
            new SchemaField("request", SchemaFieldType.Text, true, MaxLength: 32),
            new SchemaField("state", SchemaFieldType.Enum, true, Values: ["ready", "offline", "denied"])
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["request", "state"] }, MaxPayloadBytes: 96));
        registry.RegisterCommand("camera.access", "acquire", "camera.acquire", 1, "camera.access", 1, transient: true);

    }
}

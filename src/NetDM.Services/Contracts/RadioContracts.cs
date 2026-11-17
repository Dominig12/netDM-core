// Wire contracts for radio. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterRadio(SchemaRegistry registry)
    {
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

    }
}

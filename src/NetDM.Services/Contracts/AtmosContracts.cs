// Wire contracts for atmos. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterAtmos(SchemaRegistry registry)
    {
        registry.Register(new SchemaDefinition("atmos.sample", 1, 1,
        [
            new SchemaField("pressureKPa", SchemaFieldType.Number, true, 0, 1_000_000),
            new SchemaField("temperatureK", SchemaFieldType.Number, true, 0, 1_000_000),
            new SchemaField("sampleTick", SchemaFieldType.Integer, true, 0),
            new SchemaField("quality", SchemaFieldType.Enum, true, Values: ["current", "stale", "fault"]),
            // Full gas composition is part of the delivered telemetry, not a local radio copy.
            new SchemaField("gasesJson", SchemaFieldType.Text, false, MaxLength: 2000, SinceMinor: 1)
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["pressureKPa", "temperatureK", "sampleTick", "quality", "gasesJson"],
            ["panel.v1"] = ["pressureKPa", "temperatureK", "quality"]
        }, MaxPayloadBytes: 4096));

        // atmos.telemetry/sample: a subscriber asks the sensor for its latest sample; the answer is a
        // result over the network, so a late or reconnected display gets a snapshot without waiting.
        registry.Register(new SchemaDefinition("atmos.sample_request", 1, 0,
            [new SchemaField("reason", SchemaFieldType.Enum, false, Values: ["subscribe", "refresh"])],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["reason"] }, MaxPayloadBytes: 64));
        registry.RegisterCommand("atmos.telemetry", "sample", "atmos.sample_request", 1, "atmos.sample", 1);

        registry.Register(new SchemaDefinition("atmos.alarm_read", 1, 0,
            [new SchemaField("reason", SchemaFieldType.Enum, true, Values: ["inspect"])],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["reason"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("atmos.alarm_state", 1, 0,
        [
            new SchemaField("pressureKPa", SchemaFieldType.Number, true, 0, 1_000_000),
            new SchemaField("temperatureK", SchemaFieldType.Number, true, 0, 1_000_000),
            new SchemaField("danger", SchemaFieldType.Integer, true, 0, 2),
            new SchemaField("alarm", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["pressureKPa", "temperatureK", "danger", "alarm"] },
            MaxPayloadBytes: 160));
        registry.RegisterCommand("atmos.alarm", "read", "atmos.alarm_read", 1, "atmos.alarm_state", 1,
            transient: true, shellReadableResult: true);
        registry.Register(new SchemaDefinition("atmos.alert_request", 1, 0,
        [
            new SchemaField("ticket", SchemaFieldType.Text, true, MaxLength: 40),
            new SchemaField("enabled", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["ticket", "enabled"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("atmos.alert_result", 1, 0,
        [
            new SchemaField("ticket", SchemaFieldType.Text, true, MaxLength: 40),
            new SchemaField("sourceActive", SchemaFieldType.Boolean, true),
            new SchemaField("zoneAlarm", SchemaFieldType.Boolean, true),
            new SchemaField("changed", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["ticket", "sourceActive", "zoneAlarm", "changed"] }, MaxPayloadBytes: 192));
        registry.RegisterCommand("atmos.alarm", "set_alert", "atmos.alert_request", 1, "atmos.alert_result", 1);

    }
}

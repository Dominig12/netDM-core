// Wire contracts for machines. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterMachines(SchemaRegistry registry)
    {
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

        // The device UI uses a world-issued ticket. The result confirms that the action was queued.
        registry.Register(new SchemaDefinition("machine.door_control_request", 1, 0,
        [
            new SchemaField("ticket", SchemaFieldType.Text, true, MaxLength: 40),
            new SchemaField("action", SchemaFieldType.Enum, true, Values: ["open", "close", "bolt", "unbolt"])
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["ticket", "action"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("machine.door_control_accepted", 1, 0,
        [
            new SchemaField("ticket", SchemaFieldType.Text, true, MaxLength: 40),
            new SchemaField("accepted", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["ticket", "accepted"] }, MaxPayloadBytes: 128));
        registry.RegisterCommand("machine.door", "control", "machine.door_control_request", 1, "machine.door_control_accepted", 1);
    }
}

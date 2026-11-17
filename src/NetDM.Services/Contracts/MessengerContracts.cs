// Wire contracts for messenger. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterMessenger(SchemaRegistry registry)
    {
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

    }
}

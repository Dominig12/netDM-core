// Wire contracts for shell. Keep schema definitions near their service domain.
using NetDM.Services.Shell;

namespace NetDM.Services;

public sealed partial class SchemaRegistry
{
    private static void RegisterShell(SchemaRegistry registry)
    {
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
                ["ok", "parse_error", "unknown_command", "invalid_argument", "fuel_exhausted", "limit_exceeded", "source_failed",
                 "no_presence", "suspended", "out_of_reach", "moving"]),
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
            new SchemaField("ensure", SchemaFieldType.Boolean, false),
            // The program a device's media last knew it as: a copy of an entity that moved is refused ("moved").
            new SchemaField("entity", SchemaFieldType.Integer, false, 1, 1_000_000_000)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["name", "source", "ensure", "entity"] }, MaxPayloadBytes: 4096));
        registry.Register(new SchemaDefinition("shell.stop_request", 1, 0,
            [new SchemaField("program", SchemaFieldType.Integer, true, 1, 1_000_000_000)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["program"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("shell.program", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values:
                ["started", "running", "stopped", "unknown_program", "parse_error", "unknown_command", "invalid_argument", "limit_exceeded",
                 "migrated", "moved", "busy", "unreachable", "forbidden"]),
            new SchemaField("program", SchemaFieldType.Integer, false, 0, 1_000_000_000),
            new SchemaField("error", SchemaFieldType.Text, false, MaxLength: 200),
            new SchemaField("position", SchemaFieldType.Integer, false, 0, 4096),
            // Where an entity now runs ("migrated", "moved") and its generation there.
            new SchemaField("to", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("generation", SchemaFieldType.Integer, false, 1, 1_000_000)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "program", "error", "position", "to", "generation"] }, MaxPayloadBytes: 512));
        registry.RegisterCommand("network.shell", "start", "shell.program_request", 1, "shell.program", 1);
        registry.RegisterCommand("network.shell", "stop", "shell.stop_request", 1, "shell.program", 1);
        // P12.1 network space: an operator scans from a device's shell and reads what it knows as pages of
        // changes. Items are "n|id|role|x|y|z", "l|a|b|state", "s|instance|type|node" with "|age|fresh" or
        // "|age|stale" appended, and "r|<key>" for an item the operator forgot.
        registry.Register(new SchemaDefinition("shell.scan_request", 1, 0,
            [new SchemaField("radius", SchemaFieldType.Integer, false, 1, 8)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["radius"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("shell.scan", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "partial", "busy", "suspended", "out_of_reach", "moving"]),
            new SchemaField("revision", SchemaFieldType.Integer, true, 0),
            new SchemaField("nodes", SchemaFieldType.Integer, true, 0, 4096),
            new SchemaField("links", SchemaFieldType.Integer, true, 0, 8192),
            new SchemaField("services", SchemaFieldType.Integer, true, 0, 4096),
            new SchemaField("retryAfter", SchemaFieldType.Integer, false, 0, 3600)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "revision", "nodes", "links", "services", "retryAfter"] },
            MaxPayloadBytes: 256));
        registry.Register(new SchemaDefinition("shell.knowledge_request", 1, 0,
        [
            new SchemaField("since", SchemaFieldType.Integer, false, 0),
            new SchemaField("limit", SchemaFieldType.Integer, false, 1, 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["since", "limit"] }, MaxPayloadBytes: 96));
        registry.Register(new SchemaDefinition("shell.knowledge", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values: ["complete", "partial", "reset"]),
            new SchemaField("revision", SchemaFieldType.Integer, true, 0),
            new SchemaField("count", SchemaFieldType.Integer, true, 0, 64),
            new SchemaField("items", SchemaFieldType.Text, true, MaxLength: 3600)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "revision", "count", "items"] }, MaxPayloadBytes: 4096));
        registry.RegisterCommand("network.shell", "scan", "shell.scan_request", 1, "shell.scan", 1);
        // P12.2 presence: one network entity per operator, on a device's shell; "act" runs a line there.
        registry.Register(new SchemaDefinition("shell.presence_request", 1, 0,
            [new SchemaField("note", SchemaFieldType.Text, false, MaxLength: 32), new SchemaField("others", SchemaFieldType.Boolean, false)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["note", "others"] }, MaxPayloadBytes: 96));
        registry.Register(new SchemaDefinition("shell.presence", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values:
                ["entered", "present", "resumed", "suspended", "left", "ended", "none", "limit_exceeded",
                 "moving", "no_presence", "out_of_reach", "forbidden", "unreachable", "invalid_argument", "cancelled", "not_moving"]),
            new SchemaField("presence", SchemaFieldType.Integer, false, 0, 1_000_000_000),
            new SchemaField("host", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("generation", SchemaFieldType.Integer, false, 1, 1_000_000),
            new SchemaField("state", SchemaFieldType.Enum, false, Values: ["active", "suspended", "ended", "moving"]),
            new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 64),
            // P12.3: where a moving presence goes and in how many seconds it arrives.
            new SchemaField("to", SchemaFieldType.Text, false, MaxLength: 64),
            new SchemaField("arriveIn", SchemaFieldType.Integer, false, 0, 60),
            new SchemaField("travelSeconds", SchemaFieldType.Integer, false, 1, 15),
            new SchemaField("route", SchemaFieldType.Text, false, MaxLength: 672),
            new SchemaField("radioLegs", SchemaFieldType.Text, false, MaxLength: 128),
            new SchemaField("limited", SchemaFieldType.Boolean, false),
            new SchemaField("breakKind", SchemaFieldType.Enum, false, Values: ["cable", "radio", "endpoint"]),
            new SchemaField("breakFrom", SchemaFieldType.Text, false, MaxLength: 20),
            new SchemaField("breakTo", SchemaFieldType.Text, false, MaxLength: 20),
            // Up to 64 occupied nodes, each as a 20-digit node ID, a separator and a count.
            new SchemaField("others", SchemaFieldType.Text, false, MaxLength: 1472),
            // P13: the operator's identity in the network, flat so every field stays scalar.
            new SchemaField("personaName", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("personaStatus", SchemaFieldType.Text, false, MaxLength: 32),
                new SchemaField("personaRole", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("masked", SchemaFieldType.Boolean, false),
            new SchemaField("personaSignature", SchemaFieldType.Text, false, MaxLength: 4),
            new SchemaField("personaColour", SchemaFieldType.Integer, false, 0, 7),
            // P13: an intruder standing on the presence node.
            new SchemaField("threat", SchemaFieldType.Text, false, MaxLength: 24),
            // P13: the wire node the presence acts from, for tools that seed events there.
            new SchemaField("node", SchemaFieldType.Text, false, MaxLength: 20)
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["status", "presence", "host", "generation", "state", "reason", "to", "arriveIn", "travelSeconds",
                "route", "radioLegs", "limited", "breakKind", "breakFrom", "breakTo", "others",
                "personaName", "personaSignature", "personaColour", "personaStatus", "personaRole", "masked", "threat", "node"]
        }, MaxPayloadBytes: 3072));
        registry.Register(new SchemaDefinition("shell.move_request", 1, 0,
            [new SchemaField("to", SchemaFieldType.Text, true, MaxLength: 64)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["to"] }, MaxPayloadBytes: 128));
        registry.RegisterCommand("network.shell", "move", "shell.move_request", 1, "shell.presence", 1);
        registry.Register(new SchemaDefinition("shell.move_preview", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values:
                ["ready", "no_presence", "suspended", "moving", "out_of_reach", "invalid_argument", "forbidden", "unreachable", "limit_exceeded"]),
            new SchemaField("target", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("path", SchemaFieldType.Text, false, MaxLength: 672),
            new SchemaField("radioLegs", SchemaFieldType.Text, false, MaxLength: 128),
            new SchemaField("limited", SchemaFieldType.Boolean, false),
            new SchemaField("seconds", SchemaFieldType.Integer, false, 1, 15),
            new SchemaField("hops", SchemaFieldType.Integer, false, 0),
            new SchemaField("radioHops", SchemaFieldType.Integer, false, 0)
        ], new Dictionary<string, IReadOnlyList<string>>
        {
            ["full"] = ["status", "target", "path", "radioLegs", "limited", "seconds", "hops", "radioHops"]
        }, MaxPayloadBytes: 1280));
        registry.RegisterCommand("network.shell", "preview_move", "shell.move_request", 1, "shell.move_preview", 1, transient: true);
        foreach (var presenceCommand in new[] { "enter", "detach", "leave", "presence", "abort_move" })
            registry.RegisterCommand("network.shell", presenceCommand, "shell.presence_request", 1, "shell.presence", 1,
                transient: presenceCommand == "presence");
        registry.RegisterCommand("network.shell", "act", "shell.run_request", 1, "shell.output", 1);
        // P13 network life: the operator's own identity and the entities that live in the network. Lists
        // travel as text, the same way every other shell result carries them.
        registry.Register(new SchemaDefinition("shell.persona_request", 1, 0,
            [
                new SchemaField("name", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("signature", SchemaFieldType.Text, false, MaxLength: 4),
                new SchemaField("statusLine", SchemaFieldType.Text, false, MaxLength: 32),
                new SchemaField("role", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("colour", SchemaFieldType.Integer, false, 0, 7)
            ],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["name", "signature", "colour", "statusLine", "role"] }, MaxPayloadBytes: 160));
        registry.Register(new SchemaDefinition("shell.persona", 1, 0,
            [
                new SchemaField("status", SchemaFieldType.Enum, true, Values: ["ok", "invalid_argument"]),
                new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 16),
                new SchemaField("personaName", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("personaSignature", SchemaFieldType.Text, false, MaxLength: 4),
                new SchemaField("personaColour", SchemaFieldType.Integer, false, 0, 7),
                new SchemaField("personaStatus", SchemaFieldType.Text, false, MaxLength: 32),
                new SchemaField("personaRole", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("masked", SchemaFieldType.Boolean, false),
            ],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["full"] = ["status", "reason", "personaName", "personaSignature", "personaColour", "personaStatus", "personaRole"]
            }, MaxPayloadBytes: 192));
        registry.RegisterCommand("network.shell", "persona", "shell.persona_request", 1, "shell.persona", 1);
        registry.Register(new SchemaDefinition("shell.life_request", 1, 0,
            [new SchemaField("since", SchemaFieldType.Integer, false, 0), new SchemaField("limit", SchemaFieldType.Integer, false, 1, 64)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["since", "limit"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("shell.life", 1, 0,
            [
                new SchemaField("status", SchemaFieldType.Enum, true, Values: ["ok", "no_knowledge"]),
                // "id|name|signature|colour|goal|node|state|moves" joined by semicolons.
                new SchemaField("bots", SchemaFieldType.Text, false, MaxLength: 1984),
                // "seq|kind|node|name|text" joined by semicolons.
                new SchemaField("activity", SchemaFieldType.Text, false, MaxLength: 3936),
                // P13: other operators on known nodes: "node|name|signature|colour|state|threat".
                new SchemaField("presences", SchemaFieldType.Text, false, MaxLength: 4096),
                new SchemaField("since", SchemaFieldType.Integer, false, 0),
                new SchemaField("enabled", SchemaFieldType.Boolean, false),
                new SchemaField("personaName", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("personaSignature", SchemaFieldType.Text, false, MaxLength: 4),
                new SchemaField("personaColour", SchemaFieldType.Integer, false, 0, 7),
                new SchemaField("personaStatus", SchemaFieldType.Text, false, MaxLength: 32),
                new SchemaField("personaRole", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("masked", SchemaFieldType.Boolean, false),
            ],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["full"] = ["status", "bots", "activity", "presences", "since", "enabled", "personaName", "personaSignature", "personaColour", "personaStatus", "personaRole"]
            }, MaxPayloadBytes: 7168));
        registry.RegisterCommand("network.shell", "life", "shell.life_request", 1, "shell.life", 1, transient: true);
        registry.Register(new SchemaDefinition("shell.chat_read", 1, 0,
        [
            new SchemaField("presence", SchemaFieldType.Integer, true, 1, 1_000_000_000),
            new SchemaField("generation", SchemaFieldType.Integer, true, 1, 1_000_000),
            // P13: read the shared channel instead of the node visit.
            new SchemaField("channel", SchemaFieldType.Boolean, false)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["presence", "generation", "channel"] }, MaxPayloadBytes: 112));
        registry.Register(new SchemaDefinition("shell.chat_send", 1, 0,
        [
            new SchemaField("scope", SchemaFieldType.Text, true, MaxLength: 32),
            new SchemaField("text", SchemaFieldType.Text, true, MaxLength: ChatTextMaxLength),
            new SchemaField("channel", SchemaFieldType.Boolean, false)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["scope", "text", "channel"] }, MaxPayloadBytes: 1168));
        registry.Register(new SchemaDefinition("shell.chat", 1, 0,
        [
            new SchemaField("status", SchemaFieldType.Enum, true, Values:
                ["ok", "sent", "no_presence", "suspended", "moving", "out_of_reach", "stale_chat", "rate_limited", "invalid_text"]),
            new SchemaField("node", SchemaFieldType.Text, false, MaxLength: 20),
            new SchemaField("scope", SchemaFieldType.Text, false, MaxLength: 32),
            new SchemaField("received", SchemaFieldType.Integer, false, 0),
            new SchemaField("messages", SchemaFieldType.Text, false, MaxLength: ChatSnapshotMaxLength),
            new SchemaField("channel", SchemaFieldType.Boolean, false)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["status", "node", "scope", "received", "messages", "channel"] }, MaxPayloadBytes: AbsoluteMaxPayloadBytes));
        registry.RegisterCommand("network.shell", "chat_read", "shell.chat_read", 1, "shell.chat", 1, transient: true);
        registry.RegisterCommand("network.shell", "chat_send", "shell.chat_send", 1, "shell.chat", 1);

        // P13.5: breaking into a node, the stolen key ring, and masking the operator''s persona.
        registry.Register(new SchemaDefinition("shell.hack_start", 1, 0,
            [new SchemaField("node", SchemaFieldType.Text, true, MaxLength: 20)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["node"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("shell.hack_answer", 1, 0,
            [new SchemaField("code", SchemaFieldType.Text, true, MaxLength: 16)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["code"] }, MaxPayloadBytes: 64));
        registry.Register(new SchemaDefinition("shell.hack_keys", 1, 0,
            [new SchemaField("since", SchemaFieldType.Integer, false, 0)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["since"] }, MaxPayloadBytes: 32));
        registry.Register(new SchemaDefinition("shell.hack_use", 1, 0,
            [
                new SchemaField("node", SchemaFieldType.Text, true, MaxLength: 20),
                new SchemaField("purpose", SchemaFieldType.Text, false, MaxLength: 24)
            ],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["node", "purpose"] }, MaxPayloadBytes: 96));
        registry.Register(new SchemaDefinition("shell.mask", 1, 0,
            [new SchemaField("on", SchemaFieldType.Boolean, false)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["on"] }, MaxPayloadBytes: 32));
        registry.Register(new SchemaDefinition("shell.hack", 1, 0,
            [
                new SchemaField("status", SchemaFieldType.Enum, true, Values:
                    ["challenge", "ok", "failed", "cooldown", "expired", "no_key", "no_knowledge", "unknown_node",
                     "invalid_request", "no_presence", "suspended", "moving", "out_of_reach", "no_presence_control",
                     "masked", "unmasked", "stale"]),
                new SchemaField("reason", SchemaFieldType.Text, false, MaxLength: 24),
                new SchemaField("code", SchemaFieldType.Text, false, MaxLength: 16),
                new SchemaField("target", SchemaFieldType.Text, false, MaxLength: 20),
                new SchemaField("label", SchemaFieldType.Text, false, MaxLength: 64),
                new SchemaField("node", SchemaFieldType.Text, false, MaxLength: 20),
                new SchemaField("keys", SchemaFieldType.Text, false, MaxLength: 1024),
                new SchemaField("masked", SchemaFieldType.Boolean, false),
                new SchemaField("expires", SchemaFieldType.Integer, false, 0, 1_000_000_000),
                new SchemaField("expiresIn", SchemaFieldType.Integer, false, 0, 1_000_000),
                new SchemaField("retryIn", SchemaFieldType.Integer, false, 0, 1_000_000)
            ],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["full"] = ["status", "reason", "code", "target", "label", "node", "keys", "masked", "expires", "expiresIn", "retryIn"]
            }, MaxPayloadBytes: 2048));
        registry.RegisterCommand("network.shell", "hack_start", "shell.hack_start", 1, "shell.hack", 1, transient: true);
        registry.RegisterCommand("network.shell", "hack_answer", "shell.hack_answer", 1, "shell.hack", 1);
        registry.RegisterCommand("network.shell", "hack_keys", "shell.hack_keys", 1, "shell.hack", 1, transient: true);
        registry.RegisterCommand("network.shell", "hack_use", "shell.hack_use", 1, "shell.hack", 1);
        registry.RegisterCommand("network.shell", "mask", "shell.mask", 1, "shell.hack", 1);
        registry.Register(new SchemaDefinition("shell.alarm_poll", 1, 0,
            [new SchemaField("instance", SchemaFieldType.Text, true, MaxLength: 64)],
            new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["instance"] }, MaxPayloadBytes: 128));
        registry.Register(new SchemaDefinition("shell.alarm_action", 1, 0,
        [
            new SchemaField("instance", SchemaFieldType.Text, true, MaxLength: 64),
            new SchemaField("ticket", SchemaFieldType.Text, true, MaxLength: 40),
            new SchemaField("enabled", SchemaFieldType.Boolean, true)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["instance", "ticket", "enabled"] }, MaxPayloadBytes: 192));
        registry.RegisterCommand("network.shell", "poll_alarm", "shell.alarm_poll", 1, "shell.output", 1, transient: true);
        registry.RegisterCommand("network.shell", "alarm_action", "shell.alarm_action", 1, "shell.output", 1);
        registry.RegisterCommand("network.shell", "knowledge", "shell.knowledge_request", 1, "shell.knowledge", 1, transient: true);

        // NetEntity migration (§14.4): the owner moves its program to another device's shell.
        registry.Register(new SchemaDefinition("shell.migrate_request", 1, 0,
        [
            new SchemaField("program", SchemaFieldType.Integer, true, 1, 1_000_000_000),
            new SchemaField("to", SchemaFieldType.Text, true, MaxLength: 64)
        ], new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["program", "to"] }, MaxPayloadBytes: 160));
        registry.RegisterCommand("network.shell", "migrate", "shell.migrate_request", 1, "shell.program", 1);

    }
}

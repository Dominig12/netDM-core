using System.Text.Json;
using NetDM.Services;

/// <summary>
/// P13 network life contracts: the shell commands an operator uses for their identity and for the
/// entities in the network are ordinary typed commands, so their arguments and results go through the
/// same no-coercion schema validation as every other service.
/// </summary>
static class NetworkLifeSmokeTests
{
    public static void Run()
    {
        var registry = SchemaRegistry.CreateDefault();
        foreach (var (command, arguments) in new[]
                 {
                     ("persona", """{"name":"operator-2","signature":"O2","colour":2}"""),
                     ("persona", """{"name":"operator-2"}"""),
                     ("persona", "{}"),
                     ("life", """{"since":"0","limit":32}"""),
                     ("life", "{}")
                 })
        {
            var definition = registry.FindCommand("network.shell", command);
            Check(definition is not null, $"network.shell/{command} is a registered command");
            using var document = JsonDocument.Parse(arguments.Replace("\"0\"", "0"));
            Check(registry.Validate(definition!.Arguments, definition.Arguments.Minor, document.RootElement, out var canonical) ==
                ServiceStatus.Ok, $"network.shell/{command} accepts {arguments}");
            Check(canonical!.Schema.Id == definition.Arguments.Id, "the canonical payload names its schema");
            var result = command == "persona"
                ? """{"status":"ok","personaName":"operator-2","personaSignature":"O2","personaColour":2}"""
                : """{"status":"ok","bots":"1|sentinel|S|1|sentinel|2|active|3","activity":"4|move|2|sentinel|sentinel moved to 2","since":4,"enabled":true,"personaName":"","personaSignature":"","personaColour":0}""";
            using var resultDocument = JsonDocument.Parse(result);
            Check(registry.Validate(definition.Result, definition.Result.Minor, resultDocument.RootElement, out _) == ServiceStatus.Ok,
                $"network.shell/{command} result is valid");
        }

        // The identity is bounded where it is declared: a longer callsign or a colour outside 0..7 is
        // refused before anything reaches the Host's state.
        foreach (var refused in new[]
                 {
                     (Command: "persona", Arguments: "{\"colour\":9}"),
                     (Command: "persona", Arguments: "{\"name\":\"this callsign is far too long for the network\"}")
                 })
        {
            var definition = registry.FindCommand("network.shell", refused.Command)!;
            using var document = JsonDocument.Parse(refused.Arguments);
            Check(registry.Validate(definition.Arguments, definition.Arguments.Minor, document.RootElement, out _) ==
                ServiceStatus.InvalidPayload, $"network.shell/{refused.Command} refuses {refused.Arguments}");
        }
        Check(registry.FindCommand("network.shell", "life")!.Arguments.Field("limit")!.Max == 64,
            "the activity page is bounded by the command's own contract");
        Console.WriteLine("P13 network life contracts (persona, life) passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Network life check failed: " + message);
    }
}

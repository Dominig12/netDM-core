using System.Text.Json;
using NetDM.Core;
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
        CheckLifeRuntime();
        Console.WriteLine("P13 network life contracts and session lifecycle passed.");
    }

    private static void CheckLifeRuntime()
    {
        var state = new NetworkLifeRuntime();
        var first = Guid.NewGuid();
        Check(state.EnsureSession(first), "first session initializes");
        Check(!state.EnsureSession(first), "same session preserves state");

        var owner = new EndpointKey("operator-1", 1);
        state.SetPersona(owner, new NetworkPersona { Name = "operator-1", Signature = "O1" });
        Check(state.Personas.ContainsKey(owner), "persona stored");

        var bot = state.Spawn(new NodeId(4), NetworkBotGoal.Intruder, "rogue", "R", 100, 5000);
        Check(bot.NextAt == 5100 && state.PopulationStarted, "explicit spawn initializes population");
        Check(state.Banish(new NodeId(5)) == 0 && state.Bots.Count == 1, "banish respects node");
        Check(state.Banish(new NodeId(4)) == 1 && state.Bots.Count == 0, "banish removes intruder");
        Check(state.PopulationStarted, "banish does not reboot population");

        state.Spawn(new NodeId(1), NetworkBotGoal.Sentinel, "sentinel", "S", 100, 5000);
        Check(state.Retire(1) == 1 && state.Bots.Count == 0, "explicit retire removes bot");
        Check(state.PopulationStarted, "retire cannot cause immediate respawn");
        Check(!state.EnsureSession(first), "same stream preserves population decision");

        for (var i = 0; i < NetworkLifeRuntime.MaxActivity + 20; i++)
            state.Record("watch", new NodeId(1), "watcher", "watch");
        Check(state.Activities.Count == NetworkLifeRuntime.MaxActivity, "activity queue remains bounded");

        for (var i = 0; i < NetworkLifeRuntime.MaxPersonas + 12; i++)
            state.SetPersona(new EndpointKey($"operator-{i + 100}", 1), new NetworkPersona { Name = "operator" });
        Check(state.Personas.Count == NetworkLifeRuntime.MaxPersonas, "persona registry remains bounded");

        Check(state.EnsureSession(Guid.NewGuid()), "session transition detected");
        Check(!state.PopulationStarted && state.Bots.Count == 0 && state.Activities.Count == 0 &&
            state.Personas.Count == 0 && state.NextBotId == 0, "new stream discards prior entities and identities");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Network life check failed: " + message);
    }
}

using NetDM.Services.Shell;

/// <summary>
/// The network shell, first slice: a line is a bounded pipeline over typed records. Every failure is an
/// answer with its place in the line; nothing is read from the network before the whole line is checked.
/// </summary>
static class ShellSmokeTests
{
    private sealed class Source : IShellSource
    {
        public int Reads;

        public IReadOnlyList<ShellRecord>? Read(string command, ShellArguments arguments, int maxRecords, out string? error)
        {
            error = null;
            Reads++;
            switch (command)
            {
                case "services":
                    var type = arguments.Named.GetValueOrDefault("type") as string;
                    return All.Where(record => type is null || (string)record["type"]! == type).Take(maxRecords).ToArray();
                case "many":
                    return Enumerable.Range(0, (int)(long)arguments.Positional[0]).Select(index => Record(("n", (long)index))).ToArray();
                case "events":
                    var key = arguments.Named.GetValueOrDefault("key") as string;
                    return new[]
                    {
                        Record(("kind", "event"), ("key", "oxygen"), ("instance", "sensor.o2"),
                            ("sequence", 12L), ("pressureKPa", 151.5), ("ack", "applied")),
                        Record(("kind", "event"), ("key", "oxygen"), ("instance", "sensor.o2"),
                            ("sequence", 13L), ("pressureKPa", 90.0), ("ack", "rejected")),
                        Record(("kind", "event"), ("key", "temperature"), ("instance", "sensor.temp"),
                            ("sequence", 14L), ("pressureKPa", 170.0), ("ack", "applied"))
                    }.Where(record => key is null || (string)record["key"]! == key).Take(maxRecords).ToArray();
                case "broken":
                    error = "no answer";
                    return null;
                default:
                    return null;
            }
        }
    }

    private sealed class Sink : IShellSink
    {
        public readonly List<string> Calls = [];

        public bool Knows(string command) => command == "invoke";

        public IReadOnlyList<ShellRecord>? Act(string command, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error)
        {
            error = null;
            if (arguments.Positional.Count != (input is null ? 2 : 1))
            {
                error = "usage";
                return null;
            }
            var targets = input is null ? [(string)arguments.Positional[0]] : input.Select(record => (string)record["instance"]!).ToArray();
            var named = string.Join(",", arguments.Named.Select(pair => $"{pair.Key}={pair.Value}"));
            foreach (var target in targets)
                Calls.Add($"{target}:{arguments.Positional[^1]}:{named}");
            return targets.Select(target => Record(("instance", target), ("status", "sent"))).ToArray();
        }
    }

    private static ShellRecord Record(params (string Name, object Value)[] fields) =>
        new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

    private static readonly ShellRecord[] All =
    [
        Record(("instance", "research.srv-b"), ("type", "research.server"), ("hops", 4L)),
        Record(("instance", "door.e30"), ("type", "machine.door"), ("hops", 1L), ("open", true)),
        Record(("instance", "research.srv-a"), ("type", "research.server"), ("hops", 2L)),
        Record(("instance", "netdiag.e4"), ("type", "network.diagnostics"), ("hops", 0L), ("load", 0.5))
    ];

    public static void Run()
    {
        var source = new Source();
        string[] Names(string line) => NetShell.Run(line, source).Records.Select(record => (string)record["instance"]!).ToArray();

        Check(Names("services").Length == 4 && NetShell.Run("", source) is { Ok: true, Records.Count: 0 }, "a command lists its records; an empty line does nothing");
        Check(Names("services -type research.server | where hops -lt 3 | sort hops | first 1").SequenceEqual(["research.srv-a"]),
            "a pipeline filters, sorts and takes");
        Check(Names("services type=research.server | sort hops -desc").SequenceEqual(["research.srv-b", "research.srv-a"]), "name=value and a switch");
        Check(Names("services | where instance -like research.*").Length == 2 && Names("services | where instance -like \"*e*\"").Length == 4 &&
            Names("services | where instance -like door").Length == 0, "like matches whole text with * for any run");
        Check(Names("services | where hops -ge 2 | where type -ne machine.door").Length == 2, "comparisons chain");
        Check(Names("services | where open -eq true").SequenceEqual(["door.e30"]) && Names("services | where open -ne true").Length == 0,
            "a record without the field matches nothing");
        Check(Names("services | where hops -eq text").Length == 0 && Names("services | where load -gt 0").SequenceEqual(["netdiag.e4"]),
            "values of different kinds are never equal; whole and fractional numbers compare");
        Check(Names("services | sort open").Last() != "door.e30" || Names("services | sort open")[0] == "door.e30", "records without the sort field come last");
        var counted = NetShell.Run("services | where hops -gt 0 | count", source);
        Check(counted is { Ok: true, Records.Count: 1 } && (long)counted.Records[0]["count"]! == 3, "count answers one record");
        var picked = NetShell.Run("services | select instance hops | last 1", source).Records.Single();
        Check(picked.Fields.Count == 2 && (string)picked["instance"]! == "netdiag.e4" && (long)picked["hops"]! == 0, "select keeps the named fields");

        // Failures are answers with a place in the line, and the network is not asked for a line that cannot run.
        var before = source.Reads;
        Check(NetShell.Run("services | explode", source) is { Status: ShellStatus.UnknownCommand, Position: 11 }, "an unknown stage is named with its position");
        Check(NetShell.Run("services | where hops", source).Status == ShellStatus.InvalidArgument &&
            NetShell.Run("services | first many", source).Status == ShellStatus.InvalidArgument &&
            NetShell.Run("where hops -lt 3", source).Status == ShellStatus.InvalidArgument, "wrong arguments are refused");
        Check(NetShell.Run("services |", source).Status == ShellStatus.ParseError && NetShell.Run("| count", source).Status == ShellStatus.ParseError &&
            NetShell.Run("services | where instance -like \"open", source).Status == ShellStatus.ParseError &&
            NetShell.Run("-type x", source).Status == ShellStatus.ParseError, "a malformed line is a parse error");
        Check(source.Reads == before, "nothing is read for a line that cannot run");
        Check(NetShell.Run("nothing", source).Status == ShellStatus.UnknownCommand && NetShell.Run("broken", source) is { Status: ShellStatus.SourceFailed, Error: "no answer" },
            "an unknown command differs from one that failed");

        // Bounds: the line, its stages, its records and its fuel.
        Check(NetShell.Run(new string('a', 2000), source).Status == ShellStatus.LimitExceeded, "a line has a length limit");
        Check(NetShell.Run("services" + string.Concat(Enumerable.Repeat(" | count", 12)), source).Status == ShellStatus.LimitExceeded, "a line has a stage limit");
        Check(NetShell.Run("many 300", source).Status == ShellStatus.LimitExceeded, "a command may not return more records than the limit");
        var tight = new ShellLimits(Fuel: 40);
        Check(NetShell.Run("many 40 | sort n", source, tight) is { Status: ShellStatus.FuelExhausted, Records.Count: 0 } &&
            NetShell.Run("many 10 | sort n", source, tight) is { Ok: true, FuelUsed: > 10 and <= 40 }, "fuel is charged per record and ends the line");
        // Actions: the last stage or the whole line; all of the input or nothing.
        var sink = new Sink();
        ShellResult Act(string line, ShellLimits? limits = null) => NetShell.Run(line, source, limits, sink);
        var alone = Act("invoke door.e30 set_state state=open");
        Check(alone is { Ok: true, Records.Count: 1 } && sink.Calls.Single() == "door.e30:set_state:state=open" && alone.FuelUsed >= 64,
            "an action alone names its target and is charged");
        sink.Calls.Clear();
        var piped = Act("services -type research.server | sort hops | invoke status reason=open");
        Check(piped is { Ok: true, Records.Count: 2 } && sink.Calls.SequenceEqual(["research.srv-a:status:reason=open", "research.srv-b:status:reason=open"]),
            "an action after a pipeline runs for each record in order");
        sink.Calls.Clear();
        Check(Act("many 5 | invoke status") is { Status: ShellStatus.LimitExceeded } && sink.Calls.Count == 0, "a pipeline too wide for an action does nothing at all");
        Check(Act("services | invoke status | count").Status == ShellStatus.UnknownCommand && sink.Calls.Count == 0, "an action is only the last stage");
        Check(Act("services -type research.server | invoke status", new ShellLimits(Fuel: 100)) is { Status: ShellStatus.FuelExhausted } && sink.Calls.Count == 0,
            "an action that cannot be paid for does not start");
        Check(Act("invoke") is { Status: ShellStatus.InvalidArgument, Error: "usage" } && NetShell.Run("invoke door.e30 set_state", source).Status == ShellStatus.UnknownCommand,
            "an action refuses wrong arguments; without a sink the shell cannot act");
        sink.Calls.Clear();
        ShellResult Script(string script, ShellLimits? limits = null) => NetShell.RunScript(script, source, limits, sink);
        var script = Script("""
            if services -type missing
              if services
                invoke door.e30 skip
              end
            end
            if services -type machine.door
              invoke door.e30 set_state state=open
            end
            services | where hops -lt 2 | count
            """);
        Check(script.Ok && (long)script.Records.Single()["count"]! == 2 &&
            sink.Calls.SequenceEqual(["door.e30:set_state:state=open"]), "nested false blocks are skipped and the last pipeline answers");
        sink.Calls.Clear();
        before = source.Reads;
        Check(Script("invoke door.e30 set_state\nif services\n  services | nope\nend") is
            { Status: ShellStatus.UnknownCommand, Position: 51 } && source.Reads == before && sink.Calls.Count == 0,
            "invalid pipeline syntax is caught before any script executes");
        Check(Script("if invoke door.e30 set_state\nend").Status == ShellStatus.InvalidArgument &&
            Script("if services\n  self").Status == ShellStatus.ParseError && Script("end").Status == ShellStatus.ParseError &&
            sink.Calls.Count == 0, "conditions are read-only and need matching blocks");
        Check(Script("many 10 | sort n\nmany 10 | sort n", new ShellLimits(Fuel: 28)).Status == ShellStatus.FuelExhausted,
            "all pipelines share fuel");
        Check(Script("invoke door.e30 set_state\nservices | invoke set_state", new ShellLimits(MaxActions: 4)).Status == ShellStatus.LimitExceeded &&
            sink.Calls.SequenceEqual(["door.e30:set_state:"]), "all pipelines share the action budget");
        Check(NetShell.Run("services | select instance instance", source).Records[0].Fields.Count == 1 &&
            NetShell.Run("services a=1 b=2", source, new ShellLimits(MaxArguments: 1)).Status == ShellStatus.ParseError,
            "repeated projections and named-argument limits are handled");
        // Variables: a line's records kept under a name and read again without asking the network.
        sink.Calls.Clear();
        var reads = source.Reads;
        var kept = Script("$labs = services -type research.server | sort hops\n$near = $labs | first 1\nif $near | where hops -lt 3\n$near | invoke status reason=open\nend\n$labs | count");
        Check(kept is { Ok: true, Records.Count: 1 } && (long)kept.Records[0]["count"]! == 2 && source.Reads == reads + 1 &&
            sink.Calls.SequenceEqual(["research.srv-a:status:reason=open"]), "a variable is read without asking the network again");
        Check(Script("$x = services\n$y | count") is { Status: ShellStatus.ParseError, Position: > 0 } &&
            Script("$x =") .Status == ShellStatus.ParseError && Script("$x = services | explode").Status == ShellStatus.UnknownCommand,
            "a variable must be set by an earlier line and needs a pipeline");
        Check(Script("if services -type no.such\n$found = services\nend\n$found | count") is { Ok: true } skipped &&
            (long)skipped.Records[0]["count"]! == 0, "a variable whose line was skipped is empty");
        Check(NetShell.Run("$x | count", source).Status == ShellStatus.InvalidArgument && Script("$x = services\n$x extra").Status == ShellStatus.InvalidArgument,
            "variables live in scripts and take no arguments");
        Check(Script(string.Join("\n", Enumerable.Range(0, 9).Select(index => $"$v{index} = self"))).Status == ShellStatus.LimitExceeded,
            "a script holds a bounded number of variables");
        Check(Script("$big = many 200\n$big | count", new ShellLimits(Fuel: 150)).Status == ShellStatus.FuelExhausted, "reading a variable costs fuel");
        var eventRule = NetShell.Run("events -key oxygen | where pressureKPa -gt 130", source);
        Check(eventRule is { Ok: true, Records.Count: 1 } &&
            (double)eventRule.Records[0]["pressureKPa"]! == 151.5 &&
            (string)eventRule.Records[0]["ack"]! == "applied",
            "typed event source supports ordinary filters and subscription keys");
        var eventPhase = NetShell.RunScript("$event = events -key oxygen | where pressureKPa -gt 130\n$event | count",
            source);
        Check(eventPhase is { Ok: true, Records.Count: 1 } &&
            (long)eventPhase.Records[0]["count"]! == 1,
            "event data can be passed into the existing script variables and pipeline runtime");
        Console.WriteLine("Network shell pipeline, scripts, variables, actions, event filtering, limits and fuel checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Shell check failed: " + message);
    }
}

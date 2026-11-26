using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// The NETRUN domain harness. It runs without BYOND, without a Host process and without Docker, in the
/// same style as <c>NetDM.SmokeTests</c>: console output, a final marker and a non-zero exit code on
/// failure. Pass an area name as the first argument to run a subset, for example <c>dotnet run -- action</c>.
/// </summary>
internal static class Program
{
    private static string _filter = string.Empty;

    private static int Main(string[] args)
    {
        _filter = args.Length > 0 ? args[0] : string.Empty;
        Console.WriteLine("NetRun.Core smoke tests");

        if (Matches("host"))
            HostLifecycleTests.Run();
        if (Matches("resources"))
            ResourceTests.Run();
        if (Matches("security"))
            SecurityTests.Run();
        if (Matches("action"))
            ActionTests.Run();
        if (Matches("effects"))
        {
            EffectTests.Run();
            EffectConsequencesTests.Run();
        }
        if (Matches("remote"))
            RemoteActionTests.Run();
        if (Matches("knowledge"))
            KnowledgeTests.Run();
        if (Matches("range"))
            RangeTests.Run();
        if (Matches("invariant"))
            InvariantTests.Run();
        if (Matches("breach"))
            BreachTests.Run();
        if (Matches("program"))
            ProgramTests.Run();
        if (Matches("source"))
            ProgramSourceTests.Run();
        if (Matches("life"))
        {
            DigitalLifeTests.Run();
            PresenceTests.Run();
        }
        if (Matches("external"))
            ExternalWorldTests.Run();
        if (Matches("agent"))
            AgentTests.Run();

        Console.WriteLine($"All NetRun.Core checks passed ({Count} assertions).");
        return Environment.ExitCode;
    }

    private static bool Matches(string area) =>
        _filter.Length == 0 || area.StartsWith(_filter, StringComparison.OrdinalIgnoreCase);
}

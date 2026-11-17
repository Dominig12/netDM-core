using NetDM.Core;
using NetDM.Services;
using NetDM.Services.Shell;

/// <summary>
/// Declarative observations use the ordinary service control plane, stay separate
/// from player subscriptions and never grant more authority than the endpoint has.
/// </summary>
static class ProgramWatchSmokeTests
{
    public static void Run()
    {
        var source = "observe atmos.test -key oxygen -projection panel.v1 -mode queue -pending 4\n" +
            "on events -key oxygen | where pressureKPa -gt 130\n$event | count";
        var parsed = ProgramWatchSyntax.Parse(source);
        Check(parsed.Ok && parsed.Watches.Count == 1 && parsed.Body.StartsWith("on events", StringComparison.Ordinal),
            "program parser keeps the ordinary event rule and strips only declarations");
        var watch = parsed.Watches[0];
        Check(watch.Alias == "oxygen" && watch.Instance == "atmos.test" &&
              watch.Projection == "panel.v1" && watch.Mode == DeliveryMode.Queue && watch.MaxPending == 4,
            "typed observation declaration");
        Check(watch.SubscriptionKey(17) == "pg.17.oxygen" && watch.SubscriptionKey(18) != watch.SubscriptionKey(17),
            "programs cannot accidentally share an alias or unwatch one another");
        Check(!ProgramWatchSyntax.Parse("observe atmos.test -key oxygen -mode latest -pending 4\non events\nself").Ok,
            "latest-value subscription cannot request a queue");
        Check(!ProgramWatchSyntax.Parse("observe atmos.test -key oxygen\n" +
            "observe atmos.other -key oxygen\non events\nself").Ok,
            "duplicate aliases are rejected before any action");
        Check(!ProgramWatchSyntax.Parse(string.Join('\n',
            Enumerable.Repeat("observe atmos.test -key oxygen", 5)) + "\non events\nself").Ok,
            "the program watch budget is enforced");
        Check(!ProgramWatchSyntax.Parse("observe atmos.test -key oxygen -mode invalid\non events\nself").Ok,
            "unknown delivery modes are rejected");

        var services = NewRuntime();
        var operatorKey = new EndpointKey("terminal", 1);
        var sensorKey = new EndpointKey("sensor", 1);
        var outsiderKey = new EndpointKey("outsider", 1);
        Attach(services, operatorKey, sensorKey, outsiderKey);
        var tools = new NetworkInteractionTools(services);
        var owner = new NetworkToolActor(operatorKey);
        var manual = tools.Execute(owner, new NetworkToolRequest(NetworkToolKind.Watch,
            watch.Instance, Key: "oxygen", Projection: watch.Projection));
        var program1 = tools.Execute(owner, new NetworkToolRequest(NetworkToolKind.Watch,
            watch.Instance, Key: watch.SubscriptionKey(17), Projection: watch.Projection,
            Mode: watch.Mode, MaxPending: watch.MaxPending));
        var program2 = tools.Execute(owner, new NetworkToolRequest(NetworkToolKind.Watch,
            watch.Instance, Key: watch.SubscriptionKey(18), Projection: watch.Projection,
            Mode: watch.Mode, MaxPending: watch.MaxPending));
        Check(manual.Status == ServiceStatus.Ok && program1.Status == ServiceStatus.Ok &&
            program2.Status == ServiceStatus.Ok && services.Capture().Subscriptions == 3,
            "program aliases have independent, transport-backed subscriptions");
        Check(services.HasSubscription(operatorKey, watch.SubscriptionKey(17), watch.Instance,
            watch.Projection, watch.Mode, watch.MaxPending),
            "reconciliation probe finds an intact lease without writing it again");
        Check(!services.HasSubscription(outsiderKey, watch.SubscriptionKey(17), watch.Instance,
            watch.Projection, watch.Mode, watch.MaxPending),
            "subscription probe cannot leak another endpoint's keys");
        Check(tools.Execute(owner, new NetworkToolRequest(NetworkToolKind.Watch,
            watch.Instance, Key: watch.SubscriptionKey(17), Projection: watch.Projection,
            Mode: watch.Mode, MaxPending: watch.MaxPending)).Status == ServiceStatus.Duplicate,
            "repeated reconciliation is idempotent");
        Check(tools.Execute(new NetworkToolActor(outsiderKey), new NetworkToolRequest(NetworkToolKind.Watch,
            watch.Instance, Key: watch.SubscriptionKey(99), Projection: watch.Projection)).Status == ServiceStatus.UnknownService,
            "programs cannot bypass the service visibility policy");
        Check(tools.Execute(owner, new NetworkToolRequest(NetworkToolKind.Unwatch, watch.SubscriptionKey(17))).Status ==
              ServiceStatus.Ok && services.Capture().Subscriptions == 2,
            "ending a program releases only its lease, not another program or a player's watch");
        Check(!services.HasSubscription(operatorKey, watch.SubscriptionKey(17), watch.Instance,
            watch.Projection, watch.Mode, watch.MaxPending),
            "stopped program's subscription is no longer discoverable by its owner");

        // Host restart creates a clean ServiceRuntime; only desired program declarations
        // are re-established, while user watches are not created by the program manager.
        var restarted = NewRuntime();
        Attach(restarted, operatorKey, sensorKey, outsiderKey);
        var restartedTools = new NetworkInteractionTools(restarted);
        Check(restarted.Capture().Subscriptions == 0, "new session never inherits subscriptions magically");
        Check(restartedTools.Execute(owner, new NetworkToolRequest(NetworkToolKind.Watch,
            watch.Instance, Key: watch.SubscriptionKey(18), Projection: watch.Projection,
            Mode: watch.Mode, MaxPending: watch.MaxPending)).Status == ServiceStatus.Ok &&
            restarted.Capture().Subscriptions == 1, "program declaration is sufficient to reattach a subscription");
        Console.WriteLine("Declarative program watch parser, isolation and service lifecycle passed.");
    }

    private static ServiceRuntime NewRuntime() => new(SchemaRegistry.CreateDefault(), () => 1);

    private static void Attach(ServiceRuntime runtime, EndpointKey terminal, EndpointKey sensor, EndpointKey outsider)
    {
        Check(runtime.AttachEndpoint(terminal, new NodeId(1), "station") == ServiceStatus.Ok, "attach terminal");
        Check(runtime.AttachEndpoint(sensor, new NodeId(2), "station") == ServiceStatus.Ok, "attach sensor");
        Check(runtime.AttachEndpoint(outsider, new NodeId(3), "foreign") == ServiceStatus.Ok, "attach outsider");
        Check(runtime.RegisterService(sensor, "atmos.test", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Ok,
            "register provider");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException("Program watch check failed: " + message);
    }
}

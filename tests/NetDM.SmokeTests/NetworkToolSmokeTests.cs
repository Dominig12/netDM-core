using System.Text.Json;
using NetDM.Core;
using NetDM.Services;

/// <summary>
/// Generic gameplay tools: one principal for all callers, typed schemas, real service
/// delivery, and no shortcut from a program/NPC into a physical DM device.
/// </summary>
static class NetworkToolSmokeTests
{
    public static void Run()
    {
        var services = new ServiceRuntime(SchemaRegistry.CreateDefault(), () => 1);
        var console = new EndpointKey("console", 1);
        var sensor = new EndpointKey("sensor", 1);
        var machine = new EndpointKey("machine", 1);
        var outsider = new EndpointKey("outsider", 1);
        Check(services.AttachEndpoint(console, new NodeId(1), "station") == ServiceStatus.Ok, "attach console");
        Check(services.AttachEndpoint(sensor, new NodeId(2), "station") == ServiceStatus.Ok, "attach sensor");
        Check(services.AttachEndpoint(machine, new NodeId(3), "station") == ServiceStatus.Ok, "attach machine");
        Check(services.AttachEndpoint(outsider, new NodeId(4), "other") == ServiceStatus.Ok, "attach outsider");
        Check(services.RegisterService(sensor, "atmos.test", "atmos.telemetry", "atmos.sample", 1, 0) ==
            ServiceStatus.Ok, "register sample");
        Check(services.RegisterService(machine, "machine.test", "machine.control", "machine.power_state", 1, 0) ==
            ServiceStatus.Ok, "register machine");

        var toolkit = new NetworkInteractionTools(services);
        var viewer = new NetworkToolActor(console);
        var server = new NetworkToolActor(sensor, Publisher: sensor);
        var watcher = toolkit.Execute(viewer, new NetworkToolRequest(NetworkToolKind.Watch,
            "atmos.test", Key: "oxygen", Projection: "panel.v1"));
        Check(watcher.Status == ServiceStatus.Ok && watcher.Subscription > 0,
            "watch selects a schema through discover and registers by endpoint");
        Check(toolkit.Execute(viewer, new NetworkToolRequest(NetworkToolKind.Watch,
            "atmos.test", Key: "oxygen", Projection: "panel.v1")).Status == ServiceStatus.Duplicate,
            "watch of same service is idempotent");

        var telemetry = JsonSerializer.SerializeToElement(new
        {
            pressureKPa = 101.3, temperatureK = 298.0, sampleTick = 5, quality = "current"
        });
        var eventResult = toolkit.Execute(server, new NetworkToolRequest(NetworkToolKind.Publish,
            "atmos.test", Payload: telemetry, Traffic: PacketTrafficClass.Background));
        Check(eventResult.Status == ServiceStatus.Ok && eventResult.Recipients == 1 && eventResult.Message > 0,
            "publish creates a routed event, not an immediate DM callback");
        Check(toolkit.Execute(new NetworkToolActor(console, Publisher: sensor),
            new NetworkToolRequest(NetworkToolKind.Publish, "atmos.test", Payload: telemetry)).Status ==
            ServiceStatus.Forbidden, "another endpoint cannot impersonate the publisher");
        Check(toolkit.Execute(new NetworkToolActor(sensor),
            new NetworkToolRequest(NetworkToolKind.Publish, "atmos.test", Payload: telemetry)).Status ==
            ServiceStatus.Forbidden, "publishing requires explicit self-owned scope");

        var control = new NetworkToolRequest(NetworkToolKind.Invoke, "machine.test", "set_power", "press.1",
            JsonSerializer.SerializeToElement(new { on = true }));
        var invoke = toolkit.Execute(viewer, control);
        Check(invoke.Status == ServiceStatus.Ok && invoke.Operation > 0,
            "generic command creates a network operation");
        Check(toolkit.Execute(viewer, control).Status == ServiceStatus.Duplicate,
            "idempotency prevents repeat gameplay effects");
        Check(toolkit.Execute(viewer, control with { Key = "press.2",
            Payload = JsonSerializer.SerializeToElement(new { on = "true" }) }).Status ==
            ServiceStatus.InvalidPayload, "command payload cannot coerce wrong field types");

        var falseHost = new NetworkToolActor(console, ResidentProvider: machine,
            ResidentInstance: "machine.test", ResidentNode: new NodeId(3));
        Check(toolkit.Execute(falseHost, control with { Key = "press.fake" }).Status ==
            ServiceStatus.Forbidden, "a claimed resident shell cannot lend authority");

        Check(toolkit.Execute(new NetworkToolActor(outsider),
            new NetworkToolRequest(NetworkToolKind.Watch, "atmos.test", Key: "spy")).Status ==
            ServiceStatus.UnknownService, "cross-domain service stays hidden");
        Check(toolkit.Execute(new NetworkToolActor(new EndpointKey("not-attached", 1)),
            control with { Key = "press.3" }).Status == ServiceStatus.UnknownEndpoint,
            "unattached NPC/program cannot fabricate an actor");

        // A real air alarm publishes changes to a typed service; watches receive ordinary
        // transport deliveries, not a direct call into another DM object.
        Check(services.RegisterService(machine, "atmos.alarm.test", "atmos.alarm", "atmos.alarm_state", 1, 0) ==
            ServiceStatus.Ok, "register alarm event stream");
        var alarmWatch = toolkit.Execute(viewer, new NetworkToolRequest(NetworkToolKind.Watch,
            "atmos.alarm.test", Key: "alarm-events"));
        Check(alarmWatch.Status == ServiceStatus.Ok && alarmWatch.Subscription > 0,
            "subscribe to atmospheric alarm transitions");
        var transition = toolkit.Execute(new NetworkToolActor(machine, Publisher: machine),
            new NetworkToolRequest(NetworkToolKind.Publish, "atmos.alarm.test",
                Payload: JsonSerializer.SerializeToElement(new
                {
                    pressureKPa = 108.5, temperatureK = 293.0, danger = 1, alarm = true
                })));
        Check(transition.Status == ServiceStatus.Ok && transition.Recipients == 1,
            "a subscribed console receives a network event from a changed alarm");
        Check(toolkit.Execute(viewer, new NetworkToolRequest(NetworkToolKind.Unwatch, "alarm-events")).Status ==
            ServiceStatus.Ok, "remove alarm observation");
        var afterUnwatch = toolkit.Execute(new NetworkToolActor(machine, Publisher: machine),
            new NetworkToolRequest(NetworkToolKind.Publish, "atmos.alarm.test",
                Payload: JsonSerializer.SerializeToElement(new
                {
                    pressureKPa = 101.3, temperatureK = 293.0, danger = 0, alarm = false
                })));
        Check(afterUnwatch.Status == ServiceStatus.Ok && afterUnwatch.Recipients == 0,
            "unwatch stops sending alarm changes to the removed subscriber");

        // Both the publication and the invocation are queued as real physical submissions.
        Check(services.TakeSubmissions(32).Length >= 2,
            "tools must use ServiceRuntime transport outbox rather than local gameplay callbacks");

        Check(toolkit.Execute(viewer, new NetworkToolRequest(NetworkToolKind.Unwatch,
            "oxygen")).Status == ServiceStatus.Ok, "unwatch removes own subscription");
        Check(toolkit.Execute(viewer, new NetworkToolRequest(NetworkToolKind.Unwatch,
            "oxygen")).Status == ServiceStatus.UnknownService, "unwatch cannot remove absent subscription");

        Console.WriteLine("Universal network action tools (invoke/watch/unwatch/publish, actor fences, outbox) passed.");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException("Network tools check failed: " + message);
    }
}

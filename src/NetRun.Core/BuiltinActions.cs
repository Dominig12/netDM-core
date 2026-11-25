using System.Globalization;
using System.Text.Json;

namespace NetRun.Core;

/// <summary>
/// The minimum perception primitives of §7.3: inspect one known host and scan what the principal may
/// discover. Both are ordinary registered actions, so a player, an NPC and later the VM get exactly the
/// same answer through the same pipeline, including the cost and the refusal reason.
/// </summary>
public sealed class InspectAction : IGameAction
{
    public ActionDescriptor Descriptor { get; } = new(
        "net.inspect", 1, ActionKind.Query, ActionCategory.Perception,
        CapabilityCategory.Read, "net.inspect", ComputeCost: 1, MemoryCost: 0, CooldownMilliseconds: 0,
        RangeHops: 8, DisplayName: "Осмотр",
        Documentation: "Читает состояние известного хоста: компоненты, ресурсы и активные эффекты.",
        Parameters: [new ActionParameter("host", ParameterType.Integer, true, "Идентификатор хоста")]);

    public string ContentDigest => "net.inspect";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) =>
        world.Host(context.TargetHost) is null
            ? new ActionValidation(ActionStatus.TargetDestroyed, "unknown_host")
            : ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope)
    {
        var host = scope.World.Host(scope.TargetHost);
        if (host is null)
            return new ActionExecution(ActionStatus.TargetDestroyed, "unknown_host");
        var resources = scope.World.Resources(host.Id);
        var effects = scope.World.Effects(host.Id, 8);
        var payload = JsonSerializer.Serialize(new
        {
            id = host.Id.Value,
            generation = host.Generation.Value,
            name = host.Name,
            hostClass = host.Class.ToString(),
            domain = host.Network?.Domain ?? string.Empty,
            wired = host.Network?.Wired ?? false,
            powered = host.Powered,
            operational = host.Operational,
            computeCapacity = host.Compute?.CapacityCu ?? 0,
            computeRate = host.Compute?.RateCuPerSecond ?? 0,
            bufferCu = resources?.BufferCu ?? 0,
            memorySlots = host.Memory?.Slots ?? 0,
            memoryHeld = resources?.MemoryHeld ?? 0,
            storageBytes = host.Storage?.Bytes ?? 0,
            security = host.Security?.Profile.ToString() ?? SecurityProfile.Standard.ToString(),
            effects = effects.Select(effect => new
            {
                kind = effect.Kind.ToString(),
                magnitude = effect.Magnitude,
                stacks = effect.Stacks,
                expiresAt = effect.ExpiresAtMilliseconds
            }).ToArray()
        });
        return new ActionExecution(ActionStatus.Ok, null, payload);
    }
}

/// <summary>
/// Discovery of the local neighbourhood. It returns only hosts the principal may discover and, when the
/// executing host declares a domain, only that domain: knowledge is local, not a global directory.
/// </summary>
public sealed class ScanAction : IGameAction
{
    public const int MaxResults = 64;

    public ActionDescriptor Descriptor { get; } = new(
        "net.scan", 1, ActionKind.Query, ActionCategory.Perception,
        CapabilityCategory.Discovery, "net.scan", ComputeCost: 2, MemoryCost: 0, CooldownMilliseconds: 2_000,
        RangeHops: 12, DisplayName: "Сканирование",
        Documentation: "Перечисляет хосты, которые видны из текущего положения в локальном домене.");

    public string ContentDigest => "net.scan";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) =>
        world.Host(context.ExecutionHost) is null
            ? new ActionValidation(ActionStatus.TargetDestroyed, "execution_host")
            : ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope)
    {
        var origin = scope.World.Host(context.ExecutionHost);
        var domain = origin?.Network?.Domain;
        var visible = scope.World.VisibleHosts(context.Principal, MaxResults);
        var found = new List<(ulong Id, ulong Generation, string Name, string Class, string Domain, bool Powered)>();
        foreach (var host in visible)
        {
            if (domain is not null && host.Network is not null &&
                !string.Equals(host.Network.Domain, domain, StringComparison.Ordinal))
                continue;
            found.Add((host.Id.Value, host.Generation.Value, host.Name, host.Class.ToString(),
                host.Network?.Domain ?? string.Empty, host.Powered));
        }
        var payload = JsonSerializer.Serialize(new
        {
            domain = domain ?? string.Empty,
            count = found.Count,
            hosts = found.Select(host => new
            {
                id = host.Id,
                generation = host.Generation,
                name = host.Name,
                hostClass = host.Class,
                domain = host.Domain,
                powered = host.Powered
            }).ToArray()
        });
        return new ActionExecution(ActionStatus.Ok, null, payload);
    }
}

/// <summary>The built-in perception actions every NETRUN deployment has before any content is loaded.</summary>
public static class BuiltinActions
{
    public static void Register(ActionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.Register(new InspectAction());
        pipeline.Register(new ScanAction());
        // The first operation that reaches the physical world. Two actions rather than one parameterised
        // command, so a context menu, a hotkey and a preset can all name exactly what they will do.
        pipeline.Register(new DoorOpenAction());
        pipeline.Register(new DoorCloseAction());
    }
}

/// <summary>Opens a door through its own handler. A door is physical, so the effect happens at the target.</summary>
public sealed class DoorOpenAction() : RemoteDoorAction(open: true);

/// <summary>Closes and bolts a door through its own handler.</summary>
public sealed class DoorCloseAction() : RemoteDoorAction(open: false);

/// <summary>Shared implementation of the door commands; only the requested state differs.</summary>
public abstract class RemoteDoorAction(bool open) : IRemoteGameAction
{
    public const string ServiceType = "machine.door";

    public ActionDescriptor Descriptor { get; } = new(
        open ? "device.door.open" : "device.door.close", 1, ActionKind.Command, ActionCategory.Connectivity,
        CapabilityCategory.Control, "device.door", ComputeCost: 2, MemoryCost: 1, CooldownMilliseconds: 1_000,
        RangeHops: 8, DisplayName: open ? "Открыть дверь" : "Закрыть дверь",
        Documentation: open
            ? "Открывает шлюз через его штатный обработчик. Требует права управления на хосте двери."
            : "Закрывает и запирает шлюз через его штатный обработчик. Требует права управления на хосте двери.",
        Parameters: [new ActionParameter("press", ParameterType.Text, false, "Идентификатор нажатия")],
        ServiceKey: RemoteDoorAction.ServiceType);

    public string ContentDigest => open ? "door|open" : "door|close";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world)
    {
        var host = world.Host(context.TargetHost);
        if (host is null)
            return new ActionValidation(ActionStatus.TargetDestroyed, "unknown_host");
        if (!host.Available)
            return new ActionValidation(ActionStatus.HostUnavailable, "target_offline");
        return host.Services is not null && host.Services.TryInstance(RemoteDoorAction.ServiceType, out _)
            ? ActionValidation.Pass
            : new ActionValidation(ActionStatus.InvalidState, "no_door_service");
    }

    /// <summary>A door is physical: only its own handler may change it, so a local commit is refused.</summary>
    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope) =>
        new(ActionStatus.Rejected, "remote_only");

    public RemoteDispatch Dispatch(in ActionContext context, INetRunDispatchScope scope)
    {
        var host = scope.TargetHostSnapshot;
        if (host?.Services is null || !host.Services.TryInstance(RemoteDoorAction.ServiceType, out var instance))
            return RemoteDispatch.Rejected(ActionStatus.InvalidState, "no_door_service");
        var arguments = JsonSerializer.Serialize(new
        {
            press = string.Create(CultureInfo.InvariantCulture, $"nr{context.OperationId.Value}"),
            state = open ? "open" : "closed",
            bolted = !open
        });
        // The transport names an operation with its own alphabet (letters, digits, dot, dash, underscore),
        // so the idempotency key is built in that alphabet rather than reusing the internal notation.
        return scope.Send(RemoteDoorAction.ServiceType, instance, "set_state",
            string.Create(CultureInfo.InvariantCulture, $"nr-{context.OperationId.Value}"), arguments);
    }
}

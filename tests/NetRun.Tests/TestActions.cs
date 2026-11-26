using NetRun.Core;

namespace NetRun.Tests;

/// <summary>
/// Test actions exercise the pipeline, not game content: one that always works, one that needs a
/// capability and a cooldown, one that the world rejects, one that applies an effect.
/// </summary>
internal sealed class ProbeAction(int computeCost = 1, int memoryCost = 0) : IGameAction
{
    public ActionDescriptor Descriptor { get; } = new("probe.read", 1, ActionKind.Query, ActionCategory.Perception,
        CapabilityCategory.Read, "probe", computeCost, memoryCost, 0, 4, "Осмотр", "Читает наблюдаемое состояние цели.");

    public string ContentDigest => "probe";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) => ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope)
    {
        scope.Emit("probe.read.done", context.OperationId.ToString());
        return ActionExecution.Ok;
    }
}

/// <summary>A command that costs compute and memory and shares a cooldown group with its copies.</summary>
internal sealed class DoorAction(string id = "door.control", string group = "door.control") : IGameAction
{
    public ActionDescriptor Descriptor { get; } = new(id, 1, ActionKind.Command, ActionCategory.Connectivity,
        CapabilityCategory.Control, group, 2, 1, 1_000, 6, "Управление дверью",
        "Открывает или закрывает шлюз через его штатный обработчик.", ServiceKey: "machine.door");

    public string ContentDigest => "door|open";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) =>
        world.Host(context.TargetHost) is null
            ? new ActionValidation(ActionStatus.TargetDestroyed, "target")
            : ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope) => ActionExecution.Ok;
}

/// <summary>An action whose authoritative change is refused by the world.</summary>
internal sealed class RejectedAction : IGameAction
{
    public ActionDescriptor Descriptor { get; } = new("probe.rejected", 1, ActionKind.Command, ActionCategory.Execution,
        CapabilityCategory.Control, "probe.rejected", 1, 0, 0, 0, "Отказ", "Всегда отклоняется миром.");

    public string ContentDigest => "rejected";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) => ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope) =>
        new(ActionStatus.Rejected, "world_says_no");
}

/// <summary>Applies a registered effect at commit, to prove that effects only travel through the registry.</summary>
internal sealed class CloakAction(int magnitude, int durationMilliseconds, string id = "effect.cloak") : IGameAction
{
    public ActionDescriptor Descriptor { get; } = new(id, 1, ActionKind.Command, ActionCategory.Effects,
        CapabilityCategory.Execution, "effect.cloak", 1, 0, 0, 3, "Маскировка",
        "Скрывает присутствие цели на время, заданное определением эффекта.");

    public string ContentDigest => $"cloak|{magnitude}|{durationMilliseconds}";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) => ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope)
    {
        var result = scope.ApplyEffect(EffectKind.Cloak, scope.TargetHost, magnitude, durationMilliseconds, "test");
        return result.Status is EffectStatus.Applied or EffectStatus.Refreshed or EffectStatus.Replaced
            ? ActionExecution.Ok
            : new ActionExecution(ActionStatus.Rejected, result.Status.ToString());
    }
}

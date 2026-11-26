using System.Text.Json;

namespace NetRun.Core;

/// <summary>
/// One ability of §8.2 expressed through the effect registry. The action owns the server-side magnitude and
/// duration; the registry enforces the definition and the target's own state takes the consequence. A player, an
/// NPC, a program and a preset all name the same action, and none of them can pass a number of damage points, a
/// duration or a removal of a critical effect (PRIM-001, PRIM-003).
/// </summary>
public abstract class HostEffectAction(
    string id,
    EffectKind kind,
    int magnitude,
    int durationMilliseconds,
    int computeCost,
    int cooldownMilliseconds,
    string displayName,
    string documentation,
    bool hostile = false,
    CapabilityCategory capability = CapabilityCategory.Control,
    int rangeHops = 8) : IGameAction
{
    public ActionDescriptor Descriptor { get; } = new(id, 1, ActionKind.Command, ActionCategory.Security,
        capability, id, ComputeCost: computeCost, MemoryCost: 0, CooldownMilliseconds: cooldownMilliseconds,
        RangeHops: rangeHops, DisplayName: displayName, Documentation: documentation, ServiceKey: null);

    public string ContentDigest => id;

    /// <summary>The effect this ability applies, for tests and for the API reference.</summary>
    public EffectKind Kind => kind;

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world)
    {
        var host = world.Host(context.TargetHost);
        if (host is null)
            return new ActionValidation(ActionStatus.TargetDestroyed, "unknown_host");
        if (!host.Available)
            return new ActionValidation(ActionStatus.HostUnavailable, "target_offline");
        // An ability reaches a host the actor actually knows about. A hidden host answers exactly like a host
        // that does not exist, so the ability cannot be used to feel for one (PRIM-002, AT-12).
        if (context.TargetHost != context.ExecutionHost &&
            !world.Knows(context.Principal, context.TargetHost, KnowledgeLevel.Observed))
            return new ActionValidation(ActionStatus.InvalidState, "not_known");
        return ActionValidation.Pass;
    }

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope)
    {
        var host = scope.World.Host(scope.TargetHost);
        if (host is null)
            return new ActionExecution(ActionStatus.TargetDestroyed, "unknown_host");
        var applied = scope.ApplyEffect(kind, scope.TargetHost, magnitude, durationMilliseconds,
            $"net.{EffectId}:{context.OperationId.Value}");
        switch (applied.Status)
        {
            case EffectStatus.Ok:
            case EffectStatus.Applied:
            case EffectStatus.Refreshed:
            case EffectStatus.Replaced:
            case EffectStatus.Stacked:
                break;
            case EffectStatus.Rejected:
                // The definition refused a second instance: a real refusal, not a silent no-op (PRIM-003).
                return new ActionExecution(ActionStatus.Rejected, "stacking");
            default:
                return new ActionExecution(ActionStatus.Rejected, applied.Status.ToString());
        }
        // A hostile ability is something the host notices. A defensive one only changes its own posture, and
        // raising an alarm for putting up one's own shield would punish the defender (§9.3).
        if (hostile)
            scope.RaiseAlert(1, $"effect:{EffectId}");
        scope.Emit($"net.effect.{EffectId}", host.Name);
        var payload = JsonSerializer.Serialize(new
        {
            kind = EffectId,
            status = applied.Status.ToString(),
            magnitude,
            durationMs = durationMilliseconds,
            applied = applied.Applied,
            alert = scope.AlertLevel().ToString()
        });
        return new ActionExecution(ActionStatus.Ok, null, payload);
    }

    private string EffectId => kind.ToString().ToLowerInvariant();
}

/// <summary>DISRUPT: an instant hit on the digital integrity of whatever core lives on the target (§8.2).</summary>
public sealed class DisruptAction() : HostEffectAction("net.disrupt", EffectKind.Damage, 10, 0,
    computeCost: 3, cooldownMilliseconds: 3_000, "Помехи",
    "Повреждает цифровую целостность ядра на известном хосте. Требует контроля над хостом; хост замечает удар "
    + "и поднимает свою тревогу.", hostile: true);

/// <summary>DRAIN: takes the target's compute buffer instead of its integrity (§8.2).</summary>
public sealed class DrainAction() : HostEffectAction("net.drain", EffectKind.Drain, 6, 3_000,
    computeCost: 2, cooldownMilliseconds: 2_000, "Откачка",
    "Забирает вычислительный буфер цели: её действия дорожают, пока буфер не восстановится. Требует контроля "
    + "над хостом.", hostile: true);

/// <summary>SHIELD: temporary resistance to incoming damage, applied to a host the actor controls (§8.2).</summary>
public sealed class ShieldAction() : HostEffectAction("net.shield", EffectKind.Shield, 12, 10_000,
    computeCost: 4, cooldownMilliseconds: 8_000, "Щит",
    "Даёт хосту временное сопротивление входящему повреждению. Ставится на свой или известный хост, который вы "
    + "контролируете; тревоги не поднимает.");

/// <summary>CLOAK: hides what stands on the host from other observers (§8.2, §9.4).</summary>
public sealed class CloakAction() : HostEffectAction("net.cloak", EffectKind.Cloak, 1, 15_000,
    computeCost: 4, cooldownMilliseconds: 10_000, "Маскировка",
    "Скрывает присутствие на хосте от чужих наблюдателей на 15 секунд. Маскировка снижает обнаружимость, но не "
    + "даёт неуязвимости: раскрытие снимает её, удар по хосту по-прежнему проходит.");

/// <summary>
/// REVEAL: makes a masked presence visible again by lighting up the host it stands on (§8.2). It is a probe paid
/// for whether or not anybody is hiding there, which is the point of a counter.
/// </summary>
public sealed class RevealAction() : HostEffectAction("net.reveal", EffectKind.Reveal, 1, 8_000,
    computeCost: 3, cooldownMilliseconds: 5_000, "Раскрытие",
    "Освещает известный хост на 8 секунд: маскированное присутствие снова видно всем наблюдателям. Стоит ресурсов "
    + "независимо от того, прятался ли кто-то.");

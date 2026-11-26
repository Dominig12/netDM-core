using System.Globalization;
using System.Text.Json;

namespace NetRun.Core;

/// <summary>How hard a breach tries (§9.2). The mode trades time and subtlety against raw force.</summary>
public enum BreachMode : byte
{
    /// <summary>Slow and quiet: wins against ordinary protection, loses against hardened hosts.</summary>
    Stealth = 1,
    /// <summary>The default attempt.</summary>
    Normal = 2,
    /// <summary>Fast and loud: wins almost anywhere, leaves a trace every time.</summary>
    Forced = 3
}

/// <summary>
/// A breach is an ordinary action, not a script-managed exploit: the server decides the outcome from the
/// target's posture and the mode, and only then issues a temporary capability. A program cannot construct an
/// access grant for itself, and the grant is revocable like any other (SEC-001, §9.2).
/// </summary>
public abstract class BreachActionBase(BreachMode mode, CapabilityCategory category) : IGameAction
{
    public const string CooldownGroup = "security.breach";

    private static readonly string[] ModeIds = ["", "stealth", "normal", "forced"];

    public static int Difficulty(SecurityProfile profile) => profile switch
    {
        SecurityProfile.Open => 0,
        SecurityProfile.Standard => 1,
        SecurityProfile.Secure => 2,
        _ => 3
    };

    public ActionDescriptor Descriptor { get; } = new(
        $"security.breach.{ModeIds[(int)mode]}", 1, ActionKind.Command, ActionCategory.Security,
        CapabilityCategory.Discovery, CooldownGroup,
        ComputeCost: mode switch { BreachMode.Stealth => 3, BreachMode.Normal => 5, _ => 8 },
        MemoryCost: 0,
        CooldownMilliseconds: 4_000,
        RangeHops: 6,
        DisplayName: mode switch
        {
            BreachMode.Stealth => "Скрытный взлом",
            BreachMode.Normal => "Взлом",
            _ => "Форсированный взлом"
        },
        Documentation: mode switch
        {
            BreachMode.Stealth => "Тихая попытка получить управление. Проходит обычную защиту, не оставляет следа, "
                + "но останавливается усиленной.",
            BreachMode.Normal => "Обычная попытка получить управление. Проходит обычную защиту, обнаруживается "
                + "на усиленной и не берёт карантин.",
            _ => "Грубая попытка получить управление. Берёт почти любую защиту, но обнаруживается всегда."
        },
        Parameters: [new ActionParameter("category", ParameterType.Enum, false, "Запрашиваемое право",
            Values: ["read", "control", "execution"])],
        ServiceKey: null);

    public string ContentDigest => $"breach|{(byte)mode}|{(byte)category}";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world)
    {
        var host = world.Host(context.TargetHost);
        if (host is null)
            return new ActionValidation(ActionStatus.TargetDestroyed, "unknown_host");
        if (!host.Available)
            return new ActionValidation(ActionStatus.HostUnavailable, "target_offline");
        if (!world.Knows(context.Principal, context.TargetHost, KnowledgeLevel.Observed))
            return new ActionValidation(ActionStatus.InvalidState, "not_known");
        return ActionValidation.Pass;
    }

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope)
    {
        var host = scope.World.Host(scope.TargetHost);
        if (host is null)
            return new ActionExecution(ActionStatus.TargetDestroyed, "unknown_host");
        var profile = host.Security?.Profile ?? SecurityProfile.Standard;
        // A host may watch its own traffic: an IDS does not stop an attempt, but a quiet one is no longer
        // quiet. It names nobody. ICE is stronger — it defends the host and always sees (§9.3, §12.2).
        var watching = host.Security?.IntrusionDetection ?? false;
        // ICE defends what it sits on (§12.2): each strain adds difficulty, and a defended host always notices
        // the attempt. It is not an extra action — the breach simply has a harder time and leaves a trace.
        var ice = 0;
        foreach (var agent in scope.World.Agents(scope.TargetHost, AgentSlots))
            if (agent.Kind == AgentKind.Ice)
                ice++;
        // A host already on high alert is harder to take: the defenders are watching its traffic (§9.3).
        var alert = scope.AlertLevel();
        var difficulty = Difficulty(profile) + Math.Min(2, ice) + (alert >= HostAlertLevel.Hostile ? 1 : 0);
        var power = (int)mode;
        var succeeded = power >= difficulty;
        // Loud modes are always noticed; a quiet one is noticed only by a host that is watching closely.
        var loud = mode == BreachMode.Forced || ice > 0;
        var detected = loud || watching || (mode == BreachMode.Normal && difficulty >= 2) ||
            (mode == BreachMode.Stealth && difficulty >= 3);
        var grantedUntil = 0L;
        var grantId = 0UL;
        if (succeeded)
        {
            var lifetime = mode == BreachMode.Forced ? 45_000L : 90_000L;
            var granted = scope.GrantCapability(category, null, lifetime, GrantSource.Breach);
            if (!granted.Allowed)
                return new ActionExecution(ActionStatus.Rejected, granted.Status.ToString());
            grantId = granted.Token.Grant.Value;
            grantedUntil = scope.NowMilliseconds + lifetime;
            // A host that was broken into can also be looked at: the break-in is the access. This is a second
            // grant of another category, not a ladder — every check still names the category it needs (§9.1).
            if (category != CapabilityCategory.Read)
                scope.GrantCapability(CapabilityCategory.Read, null, lifetime, GrantSource.Breach);
        }
        if (detected)
        {
            // A trace, not a punishment: the defender learns that something happened and where.
            scope.ApplyEffect(EffectKind.Mark, scope.TargetHost, 1, 30_000, $"breach:{context.OperationId.Value}");
            // Noticing an intrusion raises the host's own alert: a second attempt meets a warier host.
            // A forced attempt or an engaged ICE is a louder signal than a quiet probe.
            alert = scope.RaiseAlert(loud ? 2 : 1, $"breach:{ModeIds[(int)mode]}");
            // §9.4: whoever guards this host learns where the attempt arrived from — the host, never a claim
            // the intruder makes about itself, and never a name it did not earn.
            Trace(context, scope, loud);
            scope.Emit("security.breach.detected", host.Name);
        }
        if (ice > 0)
            scope.Emit("security.ice.engaged", host.Name);
        scope.Emit(succeeded ? "security.breach.granted" : "security.breach.failed", host.Name);
        var payload = JsonSerializer.Serialize(new
        {
            success = succeeded,
            detected,
            profile = profile.ToString(),
            mode = ModeIds[(int)mode],
            alert = alert.ToString(),
            category = category.ToString(),
            grant = grantId,
            expiresAt = grantedUntil
        });
        return new ActionExecution(ActionStatus.Ok, null, payload);
    }

    /// <summary>How many agents one attempt looks at: the same bound every defended-host routine uses.</summary>
    private const int AgentSlots = 8;

    /// <summary>
    /// §9.4: a defence that noticed the attempt learns the host it came from. The defenders are the owners of
    /// the ICE planted on the target — a host with no guard has nobody to tell, and the host itself is not a
    /// person. Only a host the attempt actually executed from can be traced, and the intruder is never handed
    /// a record about itself. A loud attempt is traced precisely; a quiet one leaves only the place it came from.
    /// </summary>
    private static void Trace(in ActionContext context, INetRunMutationScope scope, bool precise)
    {
        var source = context.ExecutionHost;
        if (source.IsNone || source == context.TargetHost)
            return;
        var level = precise ? KnowledgeLevel.Identified : KnowledgeLevel.Observed;
        var confidence = precise ? 90 : 55;
        foreach (var agent in scope.World.Agents(context.TargetHost, AgentSlots))
        {
            if (agent.Kind != AgentKind.Ice || agent.Owner.IsNone || agent.Owner == context.Principal)
                continue;
            scope.RememberFor(agent.Owner, source, level, confidence);
        }
    }
}

public sealed class StealthBreachAction() : BreachActionBase(BreachMode.Stealth, CapabilityCategory.Control);

public sealed class NormalBreachAction() : BreachActionBase(BreachMode.Normal, CapabilityCategory.Control);

public sealed class ForcedBreachAction() : BreachActionBase(BreachMode.Forced, CapabilityCategory.Control);

/// <summary>Human-readable name of the outcome, for the events tab and the client log.</summary>
public static class BreachReport
{
    public static string Describe(bool success, bool detected, string profile) => string.Create(
        CultureInfo.InvariantCulture,
        $"{profile}: {(success ? "разрешение получено" : "отказ")}{(detected ? ", обнаружено" : ", незаметно")}");
}

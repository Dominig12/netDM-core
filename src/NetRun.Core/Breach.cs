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
        // ICE defends what it sits on (§12.2): each strain adds difficulty, and a defended host always notices
        // the attempt. It is not an extra action — the breach simply has a harder time and leaves a trace.
        var ice = 0;
        foreach (var agent in scope.World.Agents(scope.TargetHost, 8))
            if (agent.Kind == AgentKind.Ice)
                ice++;
        var difficulty = Difficulty(profile) + Math.Min(2, ice);
        var power = (int)mode;
        var succeeded = power >= difficulty;
        // Loud modes are always noticed; a quiet one is noticed only by a host that is watching closely.
        var detected = ice > 0 || mode == BreachMode.Forced || (mode == BreachMode.Normal && difficulty >= 2) ||
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
        }
        if (detected)
        {
            // A trace, not a punishment: the defender learns that something happened and where.
            scope.ApplyEffect(EffectKind.Mark, scope.TargetHost, 1, 30_000, $"breach:{context.OperationId.Value}");
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
            category = category.ToString(),
            grant = grantId,
            expiresAt = grantedUntil
        });
        return new ActionExecution(ActionStatus.Ok, null, payload);
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

using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §9.2: a breach is an ordinary action. The server decides the outcome from the target's posture and the
/// mode, issues a temporary capability only on success, and leaves a trace when it is noticed. The first
/// complete loop of the game — discover, breach, control — is checked here.
/// </summary>
internal static class BreachTests
{
    public static void Run()
    {
        Section("breach");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var actor = runtime.NewPrincipal();
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Discovery, null, 600_000, GrantSource.Granted, PrincipalId.None);

        var open = Host(runtime, "open.1", SecurityProfile.Open);
        var standard = Host(runtime, "standard.1", SecurityProfile.Standard);
        var secure = Host(runtime, "secure.1", SecurityProfile.Secure);
        var quarantine = Host(runtime, "quarantine.1", SecurityProfile.Quarantine);
        HostId[] all = [open, standard, secure, quarantine];

        // Knowledge ages: a scenario that jumps forward re-observes the targets, exactly as a player would.
        void Pass(long milliseconds)
        {
            clock.Advance(milliseconds);
            foreach (var host in all)
                runtime.Knowledge.Remember(actor, host, KnowledgeLevel.Observed, 90);
        }

        // Seeing the host is a right of its own; knowing what it is, is a separate thing.
        foreach (var host in all)
            runtime.Authority.Grant(actor, host, CapabilityCategory.Discovery, null, 600_000, GrantSource.Granted, PrincipalId.None);
        Equal(ActionStatus.InvalidState, Run(runtime, actor, deck.Id, secure, "b-blind", "security.breach.forced").Status,
            "breaching a host the observer never discovered is refused");

        // Observation is what makes the target actionable.
        foreach (var host in all)
            runtime.Knowledge.Remember(actor, host, KnowledgeLevel.Observed, 90);

        // An open host gives in to the quietest attempt and never notices it.
        Breach(runtime, actor, deck.Id, open, "b-open", "security.breach.stealth", success: true, detected: false,
            "an open host yields without noticing");

        // The grant that came out of it is a normal capability: it authorizes, expires and can be revoked.
        Equal(AuthorizationStatus.Ok,
            runtime.Authority.Authorize(actor, open, CapabilityCategory.Control, null, SecurityProfile.Open).Status,
            "the breach granted control of the host");
        var breachGrant = runtime.Authority.All(16).Single(grant => grant.Target == open && grant.Source == GrantSource.Breach);
        clock.Advance(90_001);
        Equal(AuthorizationStatus.Expired, runtime.Authority.Recheck(
            new CapabilityToken(breachGrant.Id, actor, open, CapabilityCategory.Control, null, breachGrant.Fence)),
            "a breach grant expires like any other");

        Pass(5_000);
        Breach(runtime, actor, deck.Id, standard, "b-standard", "security.breach.normal", success: true, detected: false,
            "ordinary protection yields without watching for a normal attempt");

        Pass(5_000);
        Breach(runtime, actor, deck.Id, secure, "b-secure-quiet", "security.breach.stealth", success: false, detected: false,
            "a quiet attempt cannot open a hardened host and is not noticed");

        Pass(5_000);
        Breach(runtime, actor, deck.Id, secure, "b-secure", "security.breach.normal", success: true, detected: true,
            "a hardened host yields to a normal attempt and notices it");
        Equal(1, runtime.Effects.On(secure, 8).Length, "a noticed breach leaves a trace on the target");

        Pass(5_000);
        Breach(runtime, actor, deck.Id, quarantine, "b-quarantine", "security.breach.normal", success: false, detected: true,
            "a quarantined host resists everything but force");

        Pass(5_000);
        Breach(runtime, actor, deck.Id, quarantine, "b-quarantine-forced", "security.breach.forced", success: true,
            detected: true, "force opens a quarantined host and is always noticed");

        // One cooldown governs every mode, so a program cannot cycle through them to farm access.
        clock.Advance(1_000);
        Equal(ActionStatus.Cooldown, Run(runtime, actor, deck.Id, open, "b-cooldown", "security.breach.forced").Status,
            "all breach modes share one cooldown");

        // The loop closes: the access a breach won opens a physical door through the normal action.
        Pass(5_000);
        var door = runtime.RegisterHost(new HostDefinition("door.1", HostClass.Terminal,
            Compute: new ComputeComponent(2, 1),
            Services: new ServicesComponent(new Dictionary<string, string> { ["machine.door"] = "door.a" }))).Host!;
        runtime.Knowledge.Remember(actor, door.Id, KnowledgeLevel.Observed, 90);
        runtime.Authority.Grant(actor, door.Id, CapabilityCategory.Discovery, null, 600_000, GrantSource.Granted,
            PrincipalId.None);
        var transport = new FakeTransport();
        runtime.Actions.AttachTransport(transport);
        var doorOpen = runtime.Actions.GetAction("device.door.open")!;
        Equal(ActionStatus.Forbidden, Run(runtime, actor, deck.Id, door.Id, "b-door-denied", doorOpen).Status,
            "a door cannot be opened before the access exists");
        Breach(runtime, actor, deck.Id, door.Id, "b-door-breach", "security.breach.forced", success: true, detected: true,
            "the forced breach of the door commits");
        Equal(ActionStatus.Accepted, Run(runtime, actor, deck.Id, door.Id, "b-door-open", doorOpen).Status,
            "the breach's control right opens the door through the normal action");
        Equal(1, transport.Sends, "the door command travelled over the transport once");

        // Revoking the won right closes the loop again.
        var doorGrant = runtime.Authority.All(32)
            .Single(grant => grant.Target == door.Id && grant.Category == CapabilityCategory.Control);
        runtime.Authority.Revoke(doorGrant.Id);
        Equal(ActionStatus.Forbidden, Run(runtime, actor, deck.Id, door.Id, "b-door-revoked", doorOpen).Status,
            "a revoked breach right stops the next attempt");
    }

    private static HostId Host(NetRunRuntime runtime, string name, SecurityProfile profile) =>
        runtime.RegisterHost(new HostDefinition(name, HostClass.Server, Compute: new ComputeComponent(4, 1),
            Security: new SecurityComponent(profile, 4, 1))).Host!.Id;

    /// <summary>Runs a breach and reads its verdict from the authoritative result, never from a guess.</summary>
    private static void Breach(NetRunRuntime runtime, PrincipalId actor, HostId execution, HostId target, string key,
        string actionId, bool success, bool detected, string message)
    {
        var outcome = Run(runtime, actor, execution, target, key, actionId);
        var payload = outcome.Status switch
        {
            ActionStatus.Committed => outcome.Result,
            _ => null
        };
        Check(payload is not null, $"{message}: outcome {outcome.Status} {outcome.Reason}");
        Check(payload!.Contains($"\"success\":{(success ? "true" : "false")}", StringComparison.Ordinal),
            $"{message}: payload {payload}");
        Check(payload.Contains($"\"detected\":{(detected ? "true" : "false")}", StringComparison.Ordinal),
            $"{message}: payload {payload}");
    }

    private static ActionOutcome Run(NetRunRuntime runtime, PrincipalId actor, HostId execution, HostId target,
        string key, string actionId) => Run(runtime, actor, execution, target, key, runtime.Actions.GetAction(actionId)!);

    private static ActionOutcome Run(NetRunRuntime runtime, PrincipalId actor, HostId execution, HostId target,
        string key, IGameAction action) =>
        runtime.Actions.Execute(
            new ActionRequest(actor, execution, target, EntityId.None, ProcessId.None, CorrelationId.None, key), action);
}

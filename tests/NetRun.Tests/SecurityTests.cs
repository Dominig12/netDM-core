using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>SEC-001 and SVC-003: explicit capabilities, no widening on delegation, fencing at commit.</summary>
internal static class SecurityTests
{
    public static void Run()
    {
        Section("capabilities");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var target = runtime.RegisterHost(new HostDefinition("server.1", HostClass.Server,
            Compute: new ComputeComponent(4, 2))).Host!;
        var open = runtime.RegisterHost(new HostDefinition("terminal.open", HostClass.Terminal,
            Security: new SecurityComponent(SecurityProfile.Open, 2, 1))).Host!;
        var quarantined = runtime.RegisterHost(new HostDefinition("terminal.quarantine", HostClass.Terminal,
            Security: new SecurityComponent(SecurityProfile.Quarantine, 1, 1))).Host!;
        var actor = runtime.NewPrincipal();
        var program = runtime.NewPrincipal();

        // Nothing is implicit on a standard host.
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, target.Id, CapabilityCategory.Control, null).Status,
            "a standard host grants nothing without a grant");
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, target.Id, CapabilityCategory.Read, null).Status,
            "read is not implicit on a standard host");

        // A grant is category-specific and does not form a ladder.
        var control = runtime.Authority.Grant(actor, target.Id, CapabilityCategory.Control, null, 10_000,
            GrantSource.Granted, PrincipalId.None);
        Check(control.Allowed, "an explicit grant is issued");
        Equal(AuthorizationStatus.Ok,
            runtime.Authority.Authorize(actor, target.Id, CapabilityCategory.Control, null).Status,
            "the granted category is allowed");
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, target.Id, CapabilityCategory.Administration, null).Status,
            "control does not imply administration");
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, target.Id, CapabilityCategory.Execution, null).Status,
            "control does not imply execution");

        // Policy profiles narrow what grants can express.
        Equal(AuthorizationStatus.Granted,
            runtime.Authority.Authorize(actor, open.Id, CapabilityCategory.Read, null, SecurityProfile.Open).Status,
            "an open host answers read implicitly");
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, open.Id, CapabilityCategory.Control, null, SecurityProfile.Open).Status,
            "an open host still refuses control without a grant");
        Equal(AuthorizationStatus.PolicyDenied,
            runtime.Authority.Authorize(actor, quarantined.Id, CapabilityCategory.Read, null, SecurityProfile.Quarantine).Status,
            "a quarantined host authorizes nothing");

        // Rechecking a live token succeeds; after revocation it fails.
        Equal(AuthorizationStatus.Ok, runtime.Authority.Recheck(control.Token), "a live token rechecks");
        runtime.Authority.Revoke(control.Token.Grant);
        Equal(AuthorizationStatus.Revoked, runtime.Authority.Recheck(control.Token),
            "a revoked grant is rejected at the commit boundary");
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, target.Id, CapabilityCategory.Control, null).Status,
            "a revoked grant no longer authorizes");

        // A new grant for the same key is a different token; the old one stays dead.
        var renewed = runtime.Authority.Grant(actor, target.Id, CapabilityCategory.Control, null, 10_000,
            GrantSource.Granted, PrincipalId.None);
        Check(renewed.Allowed, "a new grant may be issued after a revoke");
        Check(renewed.Token.Grant != control.Token.Grant, "the renewed grant has a new identity");
        Equal(AuthorizationStatus.Revoked, runtime.Authority.Recheck(control.Token),
            "the token minted before the revoke is still rejected");

        // Expiry is evaluated against game time, not wall clock.
        var shortLived = runtime.Authority.Grant(actor, target.Id, CapabilityCategory.Read, null, 100,
            GrantSource.Granted, PrincipalId.None);
        clock.Advance(101);
        Equal(AuthorizationStatus.Expired, runtime.Authority.Recheck(shortLived.Token),
            "a grant expires on game time");

        // Delegation never widens.
        Equal(AuthorizationStatus.PolicyDenied,
            runtime.Authority.Grant(program, target.Id, CapabilityCategory.Administration, null, 10_000,
                GrantSource.Delegated, actor).Status,
            "a delegator cannot copy a category it does not hold");
        var delegated = runtime.Authority.Grant(program, target.Id, CapabilityCategory.Control, null, 10_000,
            GrantSource.Delegated, actor);
        Check(delegated.Allowed, "a delegator may copy a category it holds");
        Check(runtime.Authority.Get(delegated.Token.Grant)!.Source == GrantSource.Delegated,
            "a copied grant is recorded as delegated");

        // A program's effective right is the intersection of its own grant and its delegator's.
        Equal(AuthorizationStatus.Ok,
            runtime.Authority.AuthorizeChain(actor, program, target.Id, CapabilityCategory.Control, null),
            "both hold control, so the chain is allowed");
        runtime.Authority.RevokeAll(actor);
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.AuthorizeChain(actor, program, target.Id, CapabilityCategory.Control, null),
            "revoking the delegator removes the chain even while the program's grant exists");

        // Service selectors separate grants for the same host. A scoped grant never widens.
        var scopedOnly = runtime.NewPrincipal();
        var doorOnly = runtime.Authority.Grant(scopedOnly, target.Id, CapabilityCategory.Control, "machine.door",
            10_000, GrantSource.Granted, PrincipalId.None);
        Check(doorOnly.Allowed, "a service-scoped grant is issued");
        Equal(AuthorizationStatus.Ok,
            runtime.Authority.Authorize(scopedOnly, target.Id, CapabilityCategory.Control, "machine.door").Status,
            "the scoped service is allowed");
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(scopedOnly, target.Id, CapabilityCategory.Control, "atmos.pump").Status,
            "another service is not allowed by the same grant");

        // Revoking everything a destroyed target held leaves no dangling authority.
        runtime.Authority.Grant(actor, open.Id, CapabilityCategory.Control, null, 10_000, GrantSource.Granted, PrincipalId.None);
        runtime.DestroyHost(open.Id, open.Generation);
        Equal(AuthorizationStatus.Denied,
            runtime.Authority.Authorize(actor, open.Id, CapabilityCategory.Control, null, SecurityProfile.Open).Status,
            "a destroyed host no longer authorizes anything");
    }
}

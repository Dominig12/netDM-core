namespace NetRun.Core;

/// <summary>
/// What a subject may do (§9.1). The categories are not a ladder: Read does not imply Control, and a
/// grant for one category never authorizes another.
/// </summary>
public enum CapabilityCategory : byte
{
    Discovery, Communication, Read, Control, Execution, Administration
}

public enum GrantSource : byte
{
    /// <summary>Issued by an authorized principal through the normal administrative path.</summary>
    Granted,
    /// <summary>Narrowed copy of another grant; never wider than its source.</summary>
    Delegated,
    /// <summary>Won by a breach operation; always temporary.</summary>
    Breach,
    /// <summary>Held for the length of a lease and lost with it.</summary>
    Lease,
    /// <summary>Physically wired or plugged in: the credential is the connection itself.</summary>
    Physical
}

public readonly record struct GrantId(ulong Value)
{
    public static GrantId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// One allowance: a subject, a target, a category and an optional service selector, valid until it
/// expires or is revoked. <see cref="Fence"/> is bumped for the whole key on every grant and revoke, so
/// a token minted before a revoke can be rejected at commit even if a new grant exists.
/// </summary>
public sealed record CapabilityGrant(
    GrantId Id,
    PrincipalId Subject,
    HostId Target,
    CapabilityCategory Category,
    string? ServiceKey,
    long ExpiresAtMilliseconds,
    int Fence,
    GrantSource Source,
    PrincipalId Delegator);

/// <summary>A right to attempt an operation. The authority rechecks it at commit (SVC-003).</summary>
public readonly record struct CapabilityToken(
    GrantId Grant,
    PrincipalId Subject,
    HostId Target,
    CapabilityCategory Category,
    string? ServiceKey,
    int Fence)
{
    public static CapabilityToken None => default;

    public bool IsNone => Grant.IsNone;
}

public enum AuthorizationStatus : byte
{
    Ok, Granted, Denied, Expired, Revoked, StaleFence, PolicyDenied, UnknownGrant
}

public sealed record AuthorizationResult(AuthorizationStatus Status, CapabilityToken Token)
{
    public bool Allowed => Status is AuthorizationStatus.Ok or AuthorizationStatus.Granted;
}

public sealed record CapabilityAuthorityOptions(int MaxGrants = 32_768, int MaxFenceKeys = 65_536);

/// <summary>
/// The authoritative capability store (SEC-001, SVC-003). There is no implicit superuser: every
/// decision names a subject, a target, a category and, where relevant, a service. A grant never widens
/// beyond the grant it was delegated from, and a revocation that is ordered before a commit makes that
/// commit fail.
/// </summary>
public sealed class CapabilityAuthority(ISimulationClock clock, CapabilityAuthorityOptions? options = null)
{
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly CapabilityAuthorityOptions _options = options ?? new CapabilityAuthorityOptions();
    private readonly Dictionary<ulong, CapabilityGrant> _grants = [];
    private readonly Dictionary<string, int> _fences = new(StringComparer.Ordinal);
    private ulong _nextGrant;

    public int GrantCount => _grants.Count;

    /// <summary>
    /// Issues a grant. <paramref name="delegator"/> is the principal whose authority is copied; passing
    /// <see cref="PrincipalId.None"/> means the request came from the composition root (content setup).
    /// </summary>
    public AuthorizationResult Grant(PrincipalId subject, HostId target, CapabilityCategory category,
        string? serviceKey, long lifetimeMilliseconds, GrantSource source, PrincipalId delegator)
    {
        if (subject.IsNone || target.IsNone || !Enum.IsDefined(category) || lifetimeMilliseconds <= 0)
            return new AuthorizationResult(AuthorizationStatus.Denied, CapabilityToken.None);
        if (serviceKey is { Length: > 64 })
            return new AuthorizationResult(AuthorizationStatus.Denied, CapabilityToken.None);
        if (_grants.Count >= _options.MaxGrants)
            return new AuthorizationResult(AuthorizationStatus.Denied, CapabilityToken.None);

        // Delegation never widens: the delegator must already hold the same category on the same target
        // and, if it names a service, that service too.
        if (!delegator.IsNone)
        {
            var parent = Authorize(delegator, target, category, serviceKey);
            if (!parent.Allowed)
                return new AuthorizationResult(AuthorizationStatus.PolicyDenied, CapabilityToken.None);
            source = GrantSource.Delegated;
        }

        var key = FenceKey(subject, target, category, serviceKey);
        if (!_fences.TryGetValue(key, out var fence))
        {
            if (_fences.Count >= _options.MaxFenceKeys)
                return new AuthorizationResult(AuthorizationStatus.Denied, CapabilityToken.None);
            fence = 1;
        }
        else
        {
            fence = fence == int.MaxValue ? 1 : fence + 1;
        }
        _fences[key] = fence;

        var grant = new CapabilityGrant(new GrantId(++_nextGrant), subject, target, category, serviceKey,
            checked(_clock.NowMilliseconds + lifetimeMilliseconds), fence, source, delegator);
        _grants[grant.Id.Value] = grant;
        return new AuthorizationResult(AuthorizationStatus.Granted, TokenOf(grant));
    }

    /// <summary>Revokes exactly this grant. Any token still naming it fails its next recheck.</summary>
    public AuthorizationStatus Revoke(GrantId id)
    {
        return _grants.Remove(id.Value) ? AuthorizationStatus.Revoked : AuthorizationStatus.UnknownGrant;
    }

    /// <summary>Revokes everything a subject holds, for example when its session ends.</summary>
    public int RevokeAll(PrincipalId subject)
    {
        var revoked = 0;
        foreach (var (id, grant) in _grants.ToArray())
        {
            if (grant.Subject != subject)
                continue;
            _grants.Remove(id);
            revoked++;
        }
        return revoked;
    }

    /// <summary>Revokes every grant that names a destroyed host as its target.</summary>
    public int RevokeTarget(HostId target)
    {
        var revoked = 0;
        foreach (var (id, grant) in _grants.ToArray())
        {
            if (grant.Target != target)
                continue;
            _grants.Remove(id);
            revoked++;
        }
        return revoked;
    }

    /// <summary>
    /// Decides whether a subject may attempt an operation now. The host profile narrows what grants can
    /// express: a quarantined host authorizes nothing, a secure host never answers discovery implicitly.
    /// </summary>
    public AuthorizationResult Authorize(PrincipalId subject, HostId target, CapabilityCategory category,
        string? serviceKey, SecurityProfile profile = SecurityProfile.Standard, bool lockdown = false)
    {
        if (subject.IsNone || target.IsNone)
            return new AuthorizationResult(AuthorizationStatus.Denied, CapabilityToken.None);
        // Lockdown is not absolute: it refuses ordinary authority, but it must stay breakable and it must not
        // hide whether something is there. A breach grant and plain discovery still pass. A host that raised
        // its own alert to Lockdown (§9.3) is treated like a quarantine: no implicit authority, no defaults.
        var quarantine = profile == SecurityProfile.Quarantine || lockdown;

        var now = _clock.NowMilliseconds;
        CapabilityGrant? best = null;
        foreach (var grant in _grants.Values)
        {
            if (grant.Subject != subject || grant.Target != target || grant.Category != category)
                continue;
            // A scoped grant authorizes exactly its service. An unscoped grant authorizes any service of
            // the host, which is how a broad grant is expressed; a scoped one never widens.
            if (grant.ServiceKey is not null && !string.Equals(grant.ServiceKey, serviceKey, StringComparison.Ordinal))
                continue;
            if (grant.ExpiresAtMilliseconds <= now)
                continue;
            if (quarantine && grant.Source != GrantSource.Breach &&
                !(grant.Source == GrantSource.Granted && category == CapabilityCategory.Discovery))
                continue;
            if (best is null || grant.Fence > best.Fence)
                best = grant;
        }
        if (best is not null)
            return new AuthorizationResult(AuthorizationStatus.Ok, TokenOf(best));

        // Profile defaults: an open host answers discovery and read without or during authorization; a
        // secure host does not even answer discovery without a grant.
        if (quarantine)
            return new AuthorizationResult(AuthorizationStatus.PolicyDenied, CapabilityToken.None);
        var implicitAllowed = profile switch
        {
            SecurityProfile.Open => category is CapabilityCategory.Discovery or CapabilityCategory.Read,
            // Local discovery is how a network is found at all (§4.2): a standard host answers it without a
            // grant, while reading, control and execution still need one. Without this a standard station is
            // invisible and nothing can ever be learned about it, which makes the whole map unreachable.
            SecurityProfile.Standard => category == CapabilityCategory.Discovery,
            _ => false
        };
        return implicitAllowed
            ? new AuthorizationResult(AuthorizationStatus.Granted, CapabilityToken.None)
            : new AuthorizationResult(AuthorizationStatus.Denied, CapabilityToken.None);
    }

    /// <summary>
    /// Rechecks a token at commit. Returns <see cref="AuthorizationStatus.StaleFence"/> when the key was
    /// revoked after the token was minted, so the operation must not commit.
    /// </summary>
    public AuthorizationStatus Recheck(CapabilityToken token)
    {
        if (token.IsNone)
            return AuthorizationStatus.Denied;
        // A revoked grant is removed from the store, so the token cannot resolve any more. There is no
        // tombstone to keep, and therefore nothing that grows for the lifetime of the round.
        if (!_grants.TryGetValue(token.Grant.Value, out var grant))
            return AuthorizationStatus.Revoked;
        if (grant.Subject != token.Subject || grant.Target != token.Target || grant.Category != token.Category ||
            !string.Equals(grant.ServiceKey, token.ServiceKey, StringComparison.Ordinal))
            return AuthorizationStatus.Denied;
        if (grant.ExpiresAtMilliseconds <= _clock.NowMilliseconds)
            return AuthorizationStatus.Expired;
        if (grant.Fence != token.Fence)
            return AuthorizationStatus.StaleFence;
        return AuthorizationStatus.Ok;
    }

    /// <summary>
    /// Both the delegating principal and the program must hold the category: rights of a program never
    /// exceed the intersection of what it was given and what its delegator still has.
    /// </summary>
    public AuthorizationStatus AuthorizeChain(PrincipalId delegator, PrincipalId program, HostId target,
        CapabilityCategory category, string? serviceKey, SecurityProfile profile = SecurityProfile.Standard)
    {
        var outer = Authorize(delegator, target, category, serviceKey, profile);
        if (!outer.Allowed)
            return outer.Status;
        var inner = Authorize(program, target, category, serviceKey, profile);
        return inner.Allowed ? AuthorizationStatus.Ok : inner.Status;
    }

    public CapabilityGrant? Get(GrantId id) => _grants.TryGetValue(id.Value, out var grant) ? grant : null;

    /// <summary>Bounded read model of live grants, ordered by identifier.</summary>
    public CapabilityGrant[] All(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _grants.Count);
        var result = new CapabilityGrant[count];
        var index = 0;
        foreach (var grant in _grants.Values.OrderBy(grant => grant.Id.Value))
        {
            if (index >= count)
                break;
            result[index++] = grant;
        }
        return result;
    }

    /// <summary>Drops the bookkeeping of grants that expired long ago so the store does not grow forever.</summary>
    public int Prune(long graceMilliseconds = 60_000)
    {
        var now = _clock.NowMilliseconds - graceMilliseconds;
        var removed = 0;
        foreach (var (id, grant) in _grants.ToArray())
        {
            if (grant.ExpiresAtMilliseconds > now)
                continue;
            _grants.Remove(id);
            removed++;
        }
        return removed;
    }

    private static CapabilityToken TokenOf(CapabilityGrant grant) =>
        new(grant.Id, grant.Subject, grant.Target, grant.Category, grant.ServiceKey, grant.Fence);

    private static string FenceKey(PrincipalId subject, HostId target, CapabilityCategory category, string? serviceKey) =>
        $"{subject.Value}|{target.Value}|{(byte)category}|{serviceKey ?? "*"}";
}

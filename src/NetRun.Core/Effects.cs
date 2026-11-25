namespace NetRun.Core;

/// <summary>The registered effect vocabulary (§7.1). An effect is never an arbitrary property write.</summary>
public enum EffectKind : byte
{
    Damage, Repair, Drain, Recharge, Slow, Accelerate, Silence, Shield, Resistance,
    Cloak, Reveal, Corrupt, Jam, Mark, Disable
}

/// <summary>How a second application of the same kind on the same target behaves (PRIM-003).</summary>
public enum EffectStacking : byte
{
    /// <summary>Separate stacks up to the definition's limit.</summary>
    Stack,
    /// <summary>One instance whose duration is extended.</summary>
    Refresh,
    /// <summary>The newest application replaces the previous instance.</summary>
    Replace,
    /// <summary>One instance keeps the higher magnitude.</summary>
    Strongest,
    /// <summary>Instances coexist regardless of magnitude and do not interact.</summary>
    Independent,
    /// <summary>Only one instance of this kind may exist on the target; a second is refused.</summary>
    Exclusive
}

public enum EffectStatus : byte
{
    Ok, Applied, Refreshed, Replaced, Stacked, Rejected,
    UnknownKind, MagnitudeOutOfRange, DurationOutOfRange, TargetGone, LimitReached, UnknownEffect
}

public readonly record struct EffectInstanceId(ulong Value)
{
    public static EffectInstanceId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The server-side definition of an effect: the only place that decides how strong, how long and how far
/// an effect may be. A program supplies an intent inside these ranges and never a raw number of damage
/// points, a duration or a removal of a critical effect (PRIM-003).
/// </summary>
public sealed record EffectDefinition(
    EffectKind Kind,
    int MaxMagnitude,
    int MaxDurationMilliseconds,
    int RangeHops,
    int ComputeCost,
    bool Removable,
    EffectStacking Stacking = EffectStacking.Refresh,
    int MaxStacks = 1,
    string? Documentation = null);

/// <summary>One live effect on one target.</summary>
public sealed record EffectInstance(
    EffectInstanceId Id,
    EffectKind Kind,
    HostId Target,
    PrincipalId Source,
    int Magnitude,
    int Stacks,
    long AppliedAtMilliseconds,
    long ExpiresAtMilliseconds,
    string Origin);

public sealed record EffectResult(EffectStatus Status, EffectInstance? Effect);

public sealed record EffectRegistryOptions(int MaxEffects = 16_384, int MaxEffectsPerTarget = 32);

/// <summary>
/// The authoritative effect registry and its live instances. Every application is checked against the
/// definition, and the stacking rule is applied here rather than in the caller, so the same effect from
/// a player, an NPC and a script behaves identically (§7.1, PRIM-001).
/// </summary>
public sealed class EffectRegistry(ISimulationClock clock, EffectRegistryOptions? options = null)
{
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly EffectRegistryOptions _options = options ?? new EffectRegistryOptions();
    private readonly Dictionary<EffectKind, EffectDefinition> _definitions = [];
    private readonly Dictionary<ulong, EffectInstance> _instances = [];
    private readonly Dictionary<ulong, List<ulong>> _byTarget = [];
    private ulong _nextInstance;

    public int DefinitionCount => _definitions.Count;

    public int InstanceCount => _instances.Count;

    public void Register(EffectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Kind))
            throw new ArgumentException("Unknown effect kind.", nameof(definition));
        if (definition.MaxMagnitude is < 1 or > 1_000_000 || definition.MaxDurationMilliseconds is < 0 or > 3_600_000 ||
            definition.RangeHops is < 0 or > 64 || definition.ComputeCost is < 0 or > 10_000 ||
            definition.MaxStacks is < 1 or > 64)
            throw new ArgumentException($"Invalid definition for effect {definition.Kind}.", nameof(definition));
        if (!_definitions.TryAdd(definition.Kind, definition))
            throw new ArgumentException($"Effect {definition.Kind} is already registered.");
    }

    public EffectDefinition? Definition(EffectKind kind) => _definitions.GetValueOrDefault(kind);

    /// <summary>
    /// Applies an effect within the definition's bounds. Out-of-range magnitude or duration is refused
    /// rather than clamped, so a caller cannot discover the ceiling by probing.
    /// </summary>
    public EffectResult Apply(EffectKind kind, HostId target, PrincipalId source, int magnitude,
        int durationMilliseconds, string origin)
    {
        if (!_definitions.TryGetValue(kind, out var definition))
            return new EffectResult(EffectStatus.UnknownKind, null);
        if (target.IsNone || source.IsNone || magnitude < 1 || magnitude > definition.MaxMagnitude)
            return new EffectResult(EffectStatus.MagnitudeOutOfRange, null);
        if (durationMilliseconds < 0 || durationMilliseconds > definition.MaxDurationMilliseconds)
            return new EffectResult(EffectStatus.DurationOutOfRange, null);

        var now = _clock.NowMilliseconds;
        // A definition with a zero ceiling is instantaneous: it reports the application and keeps no
        // instance, so an instant damage or repair never occupies the per-target budget.
        if (definition.MaxDurationMilliseconds == 0)
            return new EffectResult(EffectStatus.Applied,
                new EffectInstance(EffectInstanceId.None, kind, target, source, magnitude, 1, now, now, origin));

        if (_instances.Count >= _options.MaxEffects)
            return new EffectResult(EffectStatus.LimitReached, null);
        if (!_byTarget.TryGetValue(target.Value, out var targetEffects))
        {
            targetEffects = [];
            _byTarget[target.Value] = targetEffects;
        }
        if (targetEffects.Count >= _options.MaxEffectsPerTarget)
            return new EffectResult(EffectStatus.LimitReached, null);

        var expiry = now + durationMilliseconds;
        ExpireOn(target, now);

        var existing = FindSame(kind, target, origin);
        switch (definition.Stacking)
        {
            case EffectStacking.Exclusive when existing is not null:
                return new EffectResult(EffectStatus.Rejected, existing);
            case EffectStacking.Refresh when existing is not null:
            {
                var refreshed = existing with { ExpiresAtMilliseconds = expiry, Magnitude = magnitude };
                _instances[refreshed.Id.Value] = refreshed;
                return new EffectResult(EffectStatus.Refreshed, refreshed);
            }
            case EffectStacking.Replace when existing is not null:
            {
                var replaced = existing with
                {
                    Magnitude = magnitude, ExpiresAtMilliseconds = expiry, AppliedAtMilliseconds = now,
                    Source = source, Origin = origin
                };
                _instances[replaced.Id.Value] = replaced;
                return new EffectResult(EffectStatus.Replaced, replaced);
            }
            case EffectStacking.Strongest when existing is not null:
            {
                if (existing.Magnitude >= magnitude)
                    return new EffectResult(EffectStatus.Rejected, existing);
                var stronger = existing with
                {
                    Magnitude = magnitude, ExpiresAtMilliseconds = expiry, AppliedAtMilliseconds = now,
                    Source = source, Origin = origin
                };
                _instances[stronger.Id.Value] = stronger;
                return new EffectResult(EffectStatus.Replaced, stronger);
            }
            case EffectStacking.Stack when existing is not null && existing.Stacks < definition.MaxStacks:
            {
                var stacked = existing with
                {
                    Stacks = existing.Stacks + 1, ExpiresAtMilliseconds = expiry, AppliedAtMilliseconds = now
                };
                _instances[stacked.Id.Value] = stacked;
                return new EffectResult(EffectStatus.Stacked, stacked);
            }
            case EffectStacking.Stack when existing is not null:
                return new EffectResult(EffectStatus.Rejected, existing);
        }

        var instance = new EffectInstance(new EffectInstanceId(++_nextInstance), kind, target, source, magnitude,
            1, now, expiry, origin);
        _instances[instance.Id.Value] = instance;
        _byTarget[target.Value].Add(instance.Id.Value);
        return new EffectResult(EffectStatus.Applied, instance);
    }

    private EffectInstance? FindSame(EffectKind kind, HostId target, string origin)
    {
        if (!_byTarget.TryGetValue(target.Value, out var list))
            return null;
        foreach (var id in list)
        {
            if (!_instances.TryGetValue(id, out var instance) || instance.Kind != kind)
                continue;
            // Independent instances never merge, even with the same origin.
            if (instance.Origin.Length == 0)
                continue;
            return instance;
        }
        return null;
    }

    /// <summary>Removes one effect. A definition that is not removable refuses removal.</summary>
    public EffectStatus Remove(EffectInstanceId id)
    {
        if (!_instances.TryGetValue(id.Value, out var instance))
            return EffectStatus.UnknownEffect;
        if (_definitions.TryGetValue(instance.Kind, out var definition) && !definition.Removable)
            return EffectStatus.Rejected;
        return RemoveCore(instance) ? EffectStatus.Ok : EffectStatus.UnknownEffect;
    }

    /// <summary>
    /// Removes the effects whose lifecycle ends with the thing that applied them, for example a stopped
    /// program. Effects that the definition marks as persisting stay (PRIM-003).
    /// </summary>
    public int RemoveByOrigin(string origin)
    {
        if (string.IsNullOrEmpty(origin))
            return 0;
        var removed = 0;
        foreach (var instance in _instances.Values.ToArray())
        {
            if (!string.Equals(instance.Origin, origin, StringComparison.Ordinal))
                continue;
            if (_definitions.TryGetValue(instance.Kind, out var definition) && !definition.Removable)
                continue;
            if (RemoveCore(instance))
                removed++;
        }
        return removed;
    }

    /// <summary>Removes everything on a target, for example when the target host is destroyed.</summary>
    public int RemoveTarget(HostId target)
    {
        if (!_byTarget.TryGetValue(target.Value, out var list))
            return 0;
        var removed = 0;
        foreach (var id in list.ToArray())
            if (_instances.TryGetValue(id, out var instance) && RemoveCore(instance))
                removed++;
        _byTarget.Remove(target.Value);
        return removed;
    }

    private bool RemoveCore(EffectInstance instance)
    {
        if (!_instances.Remove(instance.Id.Value))
            return false;
        if (_byTarget.TryGetValue(instance.Target.Value, out var list))
            list.Remove(instance.Id.Value);
        return true;
    }

    private void ExpireOn(HostId target, long now)
    {
        if (!_byTarget.TryGetValue(target.Value, out var list))
            return;
        foreach (var id in list.ToArray())
        {
            if (_instances.TryGetValue(id, out var instance) && instance.ExpiresAtMilliseconds <= now)
                RemoveCore(instance);
        }
    }

    /// <summary>Expires every effect whose duration ran out. Called by the owner with game time.</summary>
    public int Tick(long nowMilliseconds)
    {
        var expired = 0;
        foreach (var instance in _instances.Values.ToArray())
        {
            if (instance.ExpiresAtMilliseconds > nowMilliseconds)
                continue;
            if (RemoveCore(instance))
                expired++;
        }
        return expired;
    }

    /// <summary>Live effects on a target, newest last. A bounded read model for projections.</summary>
    public EffectInstance[] On(HostId target, int limit)
    {
        if (!_byTarget.TryGetValue(target.Value, out var list))
            return [];
        var count = Math.Min(Math.Max(limit, 0), list.Count);
        var result = new EffectInstance[count];
        var index = 0;
        foreach (var id in list)
        {
            if (index >= count)
                break;
            if (_instances.TryGetValue(id, out var instance))
                result[index++] = instance;
        }
        return count == index ? result : result[..index];
    }

    /// <summary>
    /// The starting vocabulary of the MVP (§7.3). Magnitudes are authoring defaults, not balance: the
    /// definitions are configuration and content replaces them through registration.
    /// </summary>
    public static IEnumerable<EffectDefinition> MvpDefaults()
    {
        yield return new EffectDefinition(EffectKind.Damage, 40, 0, 0, 2, false, EffectStacking.Independent, 1,
            "Мгновенное повреждение целостности.");
        yield return new EffectDefinition(EffectKind.Repair, 30, 0, 0, 3, false, EffectStacking.Independent, 1,
            "Восстановление целостности из разрешённого источника.");
        yield return new EffectDefinition(EffectKind.Drain, 20, 3_000, 1, 2, true, EffectStacking.Stack, 3,
            "Расход вычислительного буфера цели.");
        yield return new EffectDefinition(EffectKind.Cloak, 1, 15_000, 0, 4, true, EffectStacking.Refresh, 1,
            "Маскировка присутствия: снижает обнаружимость, не даёт неуязвимости.");
        yield return new EffectDefinition(EffectKind.Reveal, 1, 8_000, 1, 3, true, EffectStacking.Refresh, 1,
            "Раскрытие замаскированной цели.");
        yield return new EffectDefinition(EffectKind.Shield, 20, 10_000, 0, 4, true, EffectStacking.Replace, 1,
            "Временное сопротивление входящему повреждению.");
        yield return new EffectDefinition(EffectKind.Silence, 1, 6_000, 1, 3, true, EffectStacking.Refresh, 1,
            "Временная потеря канала связи цели.");
        yield return new EffectDefinition(EffectKind.Jam, 1, 5_000, 2, 3, true, EffectStacking.Refresh, 1,
            "Ограничение связности участка сети.");
        yield return new EffectDefinition(EffectKind.Mark, 1, 60_000, 1, 1, true, EffectStacking.Independent, 1,
            "Метка наблюдения на цели.");
    }
}

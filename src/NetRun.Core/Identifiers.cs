using System.Globalization;

namespace NetRun.Core;

/// <summary>
/// A digital host of the network layer: the thing that owns components, publishes services and can
/// hold a presence. It is not a graph node (that belongs to NETDM) and not a DM object: the adapter
/// on the border maps a DM device to one host and keeps that mapping for the round.
/// </summary>
public readonly record struct HostId(ulong Value)
{
    public static HostId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An actor that can hold capabilities and spend resources: a player, an NPC, a program.</summary>
public readonly record struct PrincipalId(ulong Value)
{
    public static PrincipalId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A persistent digital identity of a character, independent of where its core currently runs.</summary>
public readonly record struct IdentityId(ulong Value)
{
    public static IdentityId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An active presence: the point a digital entity acts from. Travel does not change the identity.</summary>
public readonly record struct EntityId(ulong Value)
{
    public static EntityId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A running program instance that spends the owner's resources under its own rights.</summary>
public readonly record struct ProcessId(ulong Value)
{
    public static ProcessId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One lifecycle of one action, from creation to a terminal state.</summary>
public readonly record struct OperationId(ulong Value)
{
    public static OperationId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A generation of a host instance. Registration and destruction are idempotent; a message carrying an
/// older generation never changes a live object (HOST-001).
/// </summary>
public readonly record struct HostGeneration(ulong Value)
{
    public static HostGeneration None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A cursor into the round's operation history, stable inside one host runtime.</summary>
public readonly record struct CorrelationId(string Value)
{
    public static CorrelationId None => new(string.Empty);

    public bool IsNone => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

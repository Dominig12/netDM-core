using System.Security.Cryptography;
using System.Text;
using NetDM.Core;

namespace NetRun.Core;

/// <summary>
/// What a host is in the fiction. The class only picks defaults for authoring content; ability comes
/// from the components a host actually has, so a terminal without a compute component never runs code.
/// </summary>
public enum HostClass : byte
{
    Terminal, AccessPoint, Relay, Server, Cyberdeck, Implant, External, Virtual
}

/// <summary>
/// Default access posture of a host (§9.3). It is a policy input, not an authorization decision: the
/// decision is always made by <see cref="CapabilityAuthority"/> at commit time.
/// </summary>
public enum SecurityProfile : byte
{
    Open, Standard, Secure, Quarantine
}

/// <summary>The graph side of a host: where it stands and how it is attached.</summary>
public sealed record NetworkComponent(NodeId? Node, string Domain, bool Wired, int RadioRange, int RadioChannel);

/// <summary>Compute the host can provide: a buffer that absorbs bursts and a sustained rate (§10).</summary>
public sealed record ComputeComponent(int CapacityCu, int RateCuPerSecond);

/// <summary>Placement capacity for processes and programs. Slots are whole units.</summary>
public sealed record MemoryComponent(int Slots);

/// <summary>Virtual storage of files, data and snapshots, in bytes.</summary>
public sealed record StorageComponent(long Bytes);

/// <summary>Security posture and the limits of the defensive components installed here.</summary>
public sealed record SecurityComponent(SecurityProfile Profile, int MaxServices, int MaxPresences);

/// <summary>
/// A host as it is declared to the registry. Immutable: changing what a host is means registering a
/// new definition for a new generation, never mutating a live record behind its readers' backs.
/// </summary>
public sealed record HostDefinition(
    string Name,
    HostClass Class,
    NetworkComponent? Network = null,
    ComputeComponent? Compute = null,
    MemoryComponent? Memory = null,
    StorageComponent? Storage = null,
    SecurityComponent? Security = null)
{
    /// <summary>
    /// Stable digest of the declaration; two registrations with the same digest are idempotent. Computed
    /// on read so a record copy made with <c>with</c> never carries a stale digest.
    /// </summary>
    public string Digest => DigestOf(Name, Class, Network, Compute, Memory, Storage, Security);

    private static string DigestOf(string hostName, HostClass hostClass, NetworkComponent? network,
        ComputeComponent? compute, MemoryComponent? memory, StorageComponent? storage, SecurityComponent? security)
    {
        var text = new StringBuilder(160)
            .Append(hostName).Append('|').Append((byte)hostClass).Append('|')
            .Append(network is null
                ? "-"
                : $"{network.Node?.Value ?? 0},{network.Domain},{network.Wired},{network.RadioRange},{network.RadioChannel}")
            .Append('|')
            .Append(compute is null ? "-" : $"{compute.CapacityCu},{compute.RateCuPerSecond}").Append('|')
            .Append(memory?.Slots ?? -1).Append('|')
            .Append(storage?.Bytes ?? -1).Append('|')
            .Append(security is null ? "-" : $"{(byte)security.Profile},{security.MaxServices},{security.MaxPresences}")
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>What a reader may see of a host. Always a copy: readers never hold a live record.</summary>
public sealed record HostSnapshot(
    HostId Id,
    HostGeneration Generation,
    string Name,
    HostClass Class,
    NetworkComponent? Network,
    ComputeComponent? Compute,
    MemoryComponent? Memory,
    StorageComponent? Storage,
    SecurityComponent? Security,
    bool Powered,
    bool Operational,
    long RegisteredAtMilliseconds)
{
    /// <summary>Whether the host accepts work at all right now: the lifecycle gate everything else checks.</summary>
    public bool Available => Powered && Operational;
}

public enum HostStatus : byte
{
    Ok, Duplicate, Conflict, UnknownHost, StaleGeneration, LimitExceeded, InvalidRequest
}

public sealed record HostResult(HostStatus Status, HostSnapshot? Host)
{
    public bool Succeeded => Status is HostStatus.Ok or HostStatus.Duplicate;
}

public sealed record HostRegistryOptions(int MaxHosts = 16_384);

/// <summary>
/// The one owner of host identity and lifecycle (HOST-001, ARCH-001). Registration and destruction are
/// idempotent; a name never silently changes what it refers to, and a reused identifier gets a new
/// generation so that stale references are rejected rather than applied to the new object.
/// </summary>
public sealed class HostRegistry(ISimulationClock clock, HostRegistryOptions? options = null)
{
    private sealed class Record(HostId id, HostGeneration generation, HostDefinition definition, long registeredAt)
    {
        public readonly HostId Id = id;
        public HostGeneration Generation = generation;
        public HostDefinition Definition = definition;
        public readonly long RegisteredAt = registeredAt;
        public bool Powered = true;
        public bool Operational = true;

        public HostSnapshot Snapshot() => new(Id, Generation, Definition.Name, Definition.Class,
            Definition.Network, Definition.Compute, Definition.Memory, Definition.Storage,
            Definition.Security, Powered, Operational, RegisteredAt);
    }

    private readonly HostRegistryOptions _options = options ?? new HostRegistryOptions();
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Dictionary<ulong, Record> _byId = [];
    private readonly Dictionary<string, ulong> _byName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredNames = new(StringComparer.Ordinal);
    private ulong _nextId;

    public int Count => _byId.Count;

    /// <summary>
    /// Registers a host under its logical name. The same definition under the same name is a duplicate
    /// and returns the live record; a different definition is a conflict and changes nothing.
    /// </summary>
    public HostResult Register(HostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsValidName(definition.Name))
            return new HostResult(HostStatus.InvalidRequest, null);
        if (_byName.TryGetValue(definition.Name, out var existingId))
        {
            var existing = _byId[existingId];
            return existing.Definition.Digest == definition.Digest
                ? new HostResult(HostStatus.Duplicate, existing.Snapshot())
                : new HostResult(HostStatus.Conflict, existing.Snapshot());
        }
        if (_byId.Count >= _options.MaxHosts)
            return new HostResult(HostStatus.LimitExceeded, null);
        var id = new HostId(++_nextId);
        var record = new Record(id, new HostGeneration(1), definition, _clock.NowMilliseconds);
        _byId[id.Value] = record;
        _byName[definition.Name] = id.Value;
        _retiredNames.Remove(definition.Name);
        return new HostResult(HostStatus.Ok, record.Snapshot());
    }

    /// <summary>
    /// Binds an existing logical name to a new instance and increases its generation. Callers that still
    /// hold the previous generation are rejected: their references describe an object that is gone.
    /// </summary>
    public HostResult Rebind(string name, HostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_byName.TryGetValue(name, out var id))
            return new HostResult(HostStatus.UnknownHost, null);
        var record = _byId[id];
        record.Definition = definition with { Name = name };
        record.Generation = new HostGeneration(record.Generation.Value + 1);
        record.Powered = true;
        record.Operational = true;
        return new HostResult(HostStatus.Ok, record.Snapshot());
    }

    /// <summary>Destroys exactly this generation. A retired identifier is never handed out again.</summary>
    public HostResult Destroy(HostId id, HostGeneration generation)
    {
        if (!_byId.TryGetValue(id.Value, out var record))
            return new HostResult(HostStatus.UnknownHost, null);
        if (record.Generation != generation)
            return new HostResult(HostStatus.StaleGeneration, record.Snapshot());
        _byId.Remove(id.Value);
        _byName.Remove(record.Definition.Name);
        _retiredNames.Add(record.Definition.Name);
        return new HostResult(HostStatus.Ok, record.Snapshot());
    }

    /// <summary>Lifecycle input from the physical world: power, damage, execution availability.</summary>
    public HostResult SetLifecycle(HostId id, HostGeneration generation, bool powered, bool operational)
    {
        if (!_byId.TryGetValue(id.Value, out var record))
            return new HostResult(HostStatus.UnknownHost, null);
        if (record.Generation != generation)
            return new HostResult(HostStatus.StaleGeneration, record.Snapshot());
        record.Powered = powered;
        record.Operational = operational;
        return new HostResult(HostStatus.Ok, record.Snapshot());
    }

    public HostSnapshot? Get(HostId id) => _byId.TryGetValue(id.Value, out var record) ? record.Snapshot() : null;

    public HostSnapshot? Get(string name) =>
        _byName.TryGetValue(name, out var id) && _byId.TryGetValue(id, out var record) ? record.Snapshot() : null;

    public bool IsRetired(string name) => _retiredNames.Contains(name);

    /// <summary>Bounded, ordered read model for projections.</summary>
    public HostSnapshot[] All(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _byId.Count);
        var result = new HostSnapshot[count];
        var index = 0;
        foreach (var record in _byId.Values)
        {
            if (index >= count)
                break;
            result[index++] = record.Snapshot();
        }
        return result;
    }

    /// <summary>Every live host identity, for coordinated cleanup by the runtime owner.</summary>
    public ulong[] Identifiers()
    {
        var result = new ulong[_byId.Count];
        var index = 0;
        foreach (var value in _byId.Keys)
            result[index++] = value;
        return result;
    }

    private static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
}

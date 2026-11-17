using System.Text.Json;
using NetDM.Core;

namespace NetDM.Services;

/// <summary>One application-acknowledged, typed network event, scoped to its receiving endpoint.</summary>
public sealed record NetworkObservedEvent(long Sequence, long DeliveryId, string SubscriptionKey,
    string Instance, string Schema, JsonElement Payload, bool Applied, string? Reason);

public sealed record NetworkEventPage(long Through, long Latest, bool Overflowed,
    IReadOnlyList<NetworkObservedEvent> Events);

/// <summary>
/// Bounded inbox view for trusted network programs. It is not a second packet transport:
/// only events delivered and acknowledged by the DM endpoint enter this journal.
/// No endpoint may read another endpoint's journal or author an event through this API.
/// The owning ServiceRuntime serializes access through its existing lock.
/// </summary>
public sealed class NetworkEventJournal
{
    public const int MaxReaders = 64, MaxEventsPerReader = 32, MaxPayloadBytes = SchemaRegistry.AbsoluteMaxPayloadBytes;
    private sealed class Reader
    {
        public long Sequence;
        public readonly Queue<NetworkObservedEvent> Events = new();
    }

    private readonly Dictionary<EndpointKey, Reader> _readers = [];

    /// <summary>Replace current authorized readers. Removed endpoints immediately lose all observations.</summary>
    public void SetReaders(IEnumerable<EndpointKey> owners)
    {
        var requested = owners.Distinct().ToHashSet();
        if (requested.Count > MaxReaders)
            throw new ArgumentOutOfRangeException(nameof(owners), "Too many network event readers.");
        foreach (var old in _readers.Keys.Where(key => !requested.Contains(key)).ToArray())
            _readers.Remove(old);
        foreach (var owner in requested)
            _readers.TryAdd(owner, new Reader());
    }

    public void Remove(EndpointKey owner) => _readers.Remove(owner);

    public long Revision(EndpointKey owner) => _readers.GetValueOrDefault(owner)?.Sequence ?? 0;

    /// <summary>Called only after an application ACK of a real event delivery, regardless of the world's verdict.</summary>
    public void Record(EndpointKey receiver, long delivery, string? key, string instance, string schema,
        byte[]? payload, bool applied, string? reason)
    {
        if (!_readers.TryGetValue(receiver, out var reader) || key is not { Length: > 0 } ||
            payload is null || payload.Length > MaxPayloadBytes || reader.Sequence == long.MaxValue)
            return;
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return;
        reader.Events.Enqueue(new NetworkObservedEvent(++reader.Sequence, delivery, key, instance,
            schema, document.RootElement.Clone(), applied, reason));
        while (reader.Events.Count > MaxEventsPerReader)
            reader.Events.Dequeue();
    }

    public NetworkEventPage Read(EndpointKey owner, long after, int max)
    {
        if (!_readers.TryGetValue(owner, out var reader))
            return new(0, 0, false, []);
        max = Math.Clamp(max, 1, MaxEventsPerReader);
        var first = reader.Events.Count > 0 ? reader.Events.Peek().Sequence : reader.Sequence + 1;
        var overflowed = after < first - 1;
        var records = reader.Events.Where(item => item.Sequence > after).Take(max).ToArray();
        return new(records.Length > 0 ? records[^1].Sequence : Math.Max(0, after), reader.Sequence,
            overflowed, records);
    }
}

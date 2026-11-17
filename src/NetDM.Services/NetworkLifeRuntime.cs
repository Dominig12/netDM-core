using NetDM.Core;

namespace NetDM.Services;

/// <summary>Display identity of a network operator. Authorization remains with the endpoint.</summary>
public sealed class NetworkPersona
{
    public string Name = "", Signature = "", Status = "", Role = "";
    public int Colour;
    public long Revision;
}

/// <summary>Network-space NPC. The Host decides how and where it may move.</summary>
public sealed class NetworkBot
{
    public int Id;
    public string Name = "", Signature = "", Status = "", Role = "";
    public int Colour;
    public string BehaviorId = "";
    public bool Hostile;
    public NodeId Node;
    public string State = "active";
    public long NextAt, Fuel = 400;
    public long ThreatSince;
    public int Moves;
}

public sealed record NetworkLifeActivity(long Seq, string Kind, string Node, string Name, string Text, long At);

/// <summary>
/// Round-scoped, bounded state of NPCs and operator identities, independent of Host IPC.
/// The owning Host loop serializes mutations. Network routing remains authoritative.
/// </summary>
public sealed class NetworkLifeRuntime
{
    public const int MaxBots = 16, MaxActivity = 256, MaxPersonas = 64;

    private readonly Dictionary<EndpointKey, NetworkPersona> _personas = [];
    private readonly List<NetworkBot> _bots = [];
    private readonly Queue<NetworkLifeActivity> _activities = [];
    private Guid _stream;
    private bool _hasStream;
    private int _nextBotId;
    private long _activitySeq;

    public IReadOnlyDictionary<EndpointKey, NetworkPersona> Personas => _personas;
    public IReadOnlyList<NetworkBot> Bots => _bots;
    public IReadOnlyCollection<NetworkLifeActivity> Activities => _activities;
    public bool PopulationStarted { get; private set; }
    public int NextBotId => _nextBotId;
    public int BotCursor { get; set; }
    public long BotSerial { get; set; }
    public long NextBotPass { get; set; }
    public long NextChannelChatter { get; set; }

    /// <returns>True if this is a new transport session and prior state was discarded.</returns>
    public bool EnsureSession(Guid stream)
    {
        if (_hasStream && _stream == stream)
            return false;
        _hasStream = true;
        _stream = stream;
        _personas.Clear();
        _bots.Clear();
        _activities.Clear();
        PopulationStarted = false;
        _nextBotId = 0;
        _activitySeq = 0;
        BotCursor = 0;
        BotSerial = 0;
        NextBotPass = 0;
        NextChannelChatter = 0;
        return true;
    }

    /// <summary>Limits identities just as knowledge and presences are bounded.</summary>
    public void SetPersona(EndpointKey owner, NetworkPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        if (!_personas.ContainsKey(owner) && _personas.Count >= MaxPersonas)
            _personas.Remove(_personas.Keys.First());
        _personas[owner] = persona;
    }

    /// <summary>Explicit retirement cannot be undone by the automatic population pass.</summary>
    public int Retire(int count)
    {
        PopulationStarted = true;
        var removed = Math.Min(Math.Max(count, 0), _bots.Count);
        _bots.RemoveRange(0, removed);
        return removed;
    }

    public int Banish(NodeId? node) =>
        _bots.RemoveAll(bot => bot.Hostile && (node is null || bot.Node == node.Value));

    public void MakeRoom(int requested)
    {
        if (requested is < 0 or > MaxBots)
            throw new ArgumentOutOfRangeException(nameof(requested));
        while (_bots.Count + requested > MaxBots)
            _bots.RemoveAt(0);
    }

    public NetworkBot Spawn(NodeId node, INetworkBotBehavior behavior, long now, int firstActionDelayMs)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        if (_bots.Count >= MaxBots)
            throw new InvalidOperationException("Network bot capacity exceeded.");
        var id = ++_nextBotId;
        var bot = new NetworkBot
        {
            Id = id, Node = node, BehaviorId = behavior.Id, Hostile = behavior.Hostile,
            Colour = id % 8, Name = behavior.Name, Signature = behavior.Signature,
            NextAt = now + firstActionDelayMs
        };
        _bots.Add(bot);
        PopulationStarted = true;
        Record("spawn", node, behavior.Name, $"{behavior.Name} appeared on the network");
        return bot;
    }

    /// <summary>Limit extension-origin text before storing or publishing it.</summary>
    public static string BoundedText(string? text, int max)
    {
        if (text is null || max <= 0)
            return "";
        var length = Math.Min(text.Length, max);
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
            buffer[i] = text[i] is '|' or ';' or '\n' or '\r' or '\0' ? ' ' : text[i];
        return new string(buffer);
    }

    public void Record(string kind, NodeId node, string name, string message)
    {
        _activities.Enqueue(new NetworkLifeActivity(++_activitySeq,
            BoundedText(kind, 24), node.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BoundedText(name, 24), BoundedText(message, 160), Environment.TickCount64));
        while (_activities.Count > MaxActivity)
            _activities.Dequeue();
    }
}

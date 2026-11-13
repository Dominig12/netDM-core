using System.Security.Cryptography;
using System.Text;

namespace NetDM.Services.Research;

/// <summary>
/// One research node of the static, versioned content. Costs are whole points per point type;
/// the ledger turns them into milli-points.
/// </summary>
public sealed record ResearchNodeDefinition(
    string Id, IReadOnlyList<string> Prerequisites, IReadOnlyList<string> Designs,
    IReadOnlyDictionary<string, long> Costs, bool Starting = false, bool Hidden = false, bool Experimental = false);

/// <summary>
/// Read-only research content exported by DM: node definitions, prerequisites, prices and design IDs.
/// It is never research progress. Its hash binds a round's journal to one content version, so a
/// changed catalog cannot silently reinterpret recorded purchases.
/// </summary>
public sealed class ResearchCatalog
{
    public const int MaxNodes = 4096;
    public const int MaxListLength = 512;
    public const long MaxCost = 1_000_000_000;

    private ResearchCatalog(Dictionary<string, ResearchNodeDefinition> nodes, string[] pointTypes, string hash)
    {
        Nodes = nodes;
        PointTypes = pointTypes;
        Hash = hash;
    }

    public IReadOnlyDictionary<string, ResearchNodeDefinition> Nodes { get; }
    /// <summary>Point types in ordinal order; the first one is the primary type shown on the wire.</summary>
    public IReadOnlyList<string> PointTypes { get; }
    /// <summary>SHA-256 of the canonical content, lowercase hex.</summary>
    public string Hash { get; }
    public string ShortHash => Hash[..12];
    public string PrimaryPointType => PointTypes.Count > 0 ? PointTypes[0] : "";

    public IEnumerable<ResearchNodeDefinition> StartingNodes =>
        Nodes.Values.Where(node => node.Starting).OrderBy(node => node.Id, StringComparer.Ordinal);

    /// <summary>Validates the content and fixes its hash. Throws <see cref="ArgumentException"/> on the first defect.</summary>
    public static ResearchCatalog Create(IEnumerable<ResearchNodeDefinition> definitions, IEnumerable<string> pointTypes)
    {
        var types = pointTypes.Distinct(StringComparer.Ordinal).OrderBy(type => type, StringComparer.Ordinal).ToArray();
        if (types.Length is < 1 or > 16 || types.Any(type => !IsValidPointType(type)))
            throw new ArgumentException("A research catalog needs 1..16 valid point types.");
        var known = types.ToHashSet(StringComparer.Ordinal);
        var nodes = new Dictionary<string, ResearchNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in definitions)
        {
            if (nodes.Count >= MaxNodes)
                throw new ArgumentException($"A research catalog holds at most {MaxNodes} nodes.");
            if (!IsValidId(node.Id))
                throw new ArgumentException($"Invalid research node ID '{node.Id}'.");
            if (node.Prerequisites.Count > MaxListLength || node.Designs.Count > MaxListLength ||
                node.Prerequisites.Any(id => !IsValidId(id) || id == node.Id))
                throw new ArgumentException($"Research node '{node.Id}' has invalid prerequisites.");
            if (node.Designs.FirstOrDefault(id => !IsValidDesignId(id)) is { } design)
                throw new ArgumentException($"Research node '{node.Id}' has an invalid design ID '{design}'.");
            if (node.Costs.Any(cost => !known.Contains(cost.Key) || cost.Value is < 0 or > MaxCost))
                throw new ArgumentException($"Research node '{node.Id}' has an invalid cost.");
            if (!nodes.TryAdd(node.Id, Normalize(node)))
                throw new ArgumentException($"Research node '{node.Id}' is defined twice.");
        }
        foreach (var node in nodes.Values)
            foreach (var prerequisite in node.Prerequisites)
                if (!nodes.ContainsKey(prerequisite))
                    throw new ArgumentException($"Research node '{node.Id}' requires unknown node '{prerequisite}'.");
        return new ResearchCatalog(nodes, types, ComputeHash(nodes.Values, types));
    }

    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Design IDs are DM's opaque names and are only stored ("power control" exists): printable text.</summary>
    public static bool IsValidDesignId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => c is >= ' ' and <= '~') && id.Trim() == id;

    public static bool IsValidPointType(string? type) =>
        type is { Length: > 0 and <= 32 } && type.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-');

    private static ResearchNodeDefinition Normalize(ResearchNodeDefinition node) => node with
    {
        Prerequisites = node.Prerequisites.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        Designs = node.Designs.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        Costs = node.Costs.Where(cost => cost.Value > 0)
            .OrderBy(cost => cost.Key, StringComparer.Ordinal).ToDictionary(cost => cost.Key, cost => cost.Value, StringComparer.Ordinal)
    };

    private static string ComputeHash(IEnumerable<ResearchNodeDefinition> nodes, string[] types)
    {
        var text = new StringBuilder();
        text.Append("types=").AppendJoin(',', types).Append('\n');
        foreach (var node in nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            text.Append(node.Id).Append('|').AppendJoin(',', node.Prerequisites).Append('|').AppendJoin(',', node.Designs).Append('|');
            text.AppendJoin(',', node.Costs.Select(cost => $"{cost.Key}={cost.Value}"));
            text.Append('|').Append(node.Starting ? 's' : '-').Append(node.Hidden ? 'h' : '-').Append(node.Experimental ? 'e' : '-').Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

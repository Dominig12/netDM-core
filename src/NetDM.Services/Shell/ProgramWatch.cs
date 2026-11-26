using System.Globalization;

namespace NetDM.Services.Shell;

/// <summary>
/// A declaration at the top of a resident shell program. Its alias is local to the program:
/// the ServiceRuntime subscription uses a separate, reserved per-program key.
/// </summary>
public sealed record ProgramWatch(string Alias, string Instance, string Projection, DeliveryMode Mode, int MaxPending)
{
    public string SubscriptionKey(int programId) =>
        $"pg.{programId.ToString(CultureInfo.InvariantCulture)}.{Alias}";
}

public sealed record ProgramWatchParse(string Body, IReadOnlyList<ProgramWatch> Watches,
    string? Error = null, int Position = 0)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The optional first lines of a resident program declare subscriptions:
/// observe INSTANCE -key ALIAS [-projection PROJECTION] [-mode latest|queue] [-pending 1..16]
///
/// Declaration never performs an action by itself. Host owns its lifecycle and executes
/// ordinary ServiceRuntime.Subscribe/Unsubscribe for the principal's own endpoint.
/// </summary>
public static class ProgramWatchSyntax
{
    public const int MaxWatches = 4;
    public const int MaxAliasLength = 32;

    public static ProgramWatchParse Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = source.Split('\n');
        var offset = 0;
        var watches = new List<ProgramWatch>(MaxWatches);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!(line == "observe" || line.StartsWith("observe ", StringComparison.Ordinal) ||
                  line.StartsWith("observe\t", StringComparison.Ordinal)))
                break;
            if (watches.Count >= MaxWatches)
                return new(source, [], $"at most {MaxWatches} observe declarations", offset);
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4 || words[0] != "observe" || !ServiceNames.IsValid(words[1]))
                return new(source, [], "observe <instance> -key <alias> [-projection name] [-mode latest|queue] [-pending 1..16]", offset);
            var alias = "";
            var projection = "full";
            var mode = DeliveryMode.LatestValue;
            var pending = 1;
            var given = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 2; i < words.Length; i += 2)
            {
                if (i + 1 >= words.Length || !given.Add(words[i]))
                    return new(source, [], "duplicate or incomplete observe option", offset);
                switch (words[i])
                {
                    case "-key":
                        alias = words[i + 1];
                        break;
                    case "-projection":
                        projection = words[i + 1];
                        break;
                    case "-mode" when words[i + 1] == "latest":
                        mode = DeliveryMode.LatestValue;
                        break;
                    case "-mode" when words[i + 1] == "queue":
                        mode = DeliveryMode.Queue;
                        break;
                    case "-pending" when int.TryParse(words[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                        pending = parsed;
                        break;
                    default:
                        return new(source, [], "unknown observe option or delivery mode", offset);
                }
            }
            if (alias.Length is < 1 or > MaxAliasLength || !ServiceNames.IsValid(alias) ||
                !ServiceNames.IsValid(projection) || !aliases.Add(alias) ||
                pending is < 1 or > 16 || (mode == DeliveryMode.LatestValue && pending != 1))
                return new(source, [], "invalid or duplicated observe alias, projection, or queue capacity", offset);
            watches.Add(new ProgramWatch(alias, words[1], projection, mode, pending));
            offset += raw.Length + 1;
        }
        return new(source[Math.Min(offset, source.Length)..], watches);
    }
}

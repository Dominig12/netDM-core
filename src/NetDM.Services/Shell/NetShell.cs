using System.Globalization;

namespace NetDM.Services.Shell;

/// <summary>Why a line did not produce records. Every failure is an answer, never an exception.</summary>
public enum ShellStatus : byte
{
    Ok, ParseError, UnknownCommand, InvalidArgument, FuelExhausted, LimitExceeded, SourceFailed
}

/// <summary>
/// One record of a pipeline: named fields holding text, a whole number, a number or a flag.
/// Commands exchange records, never formatted text, so a filter compares values and not their print.
/// </summary>
public sealed class ShellRecord(IReadOnlyDictionary<string, object> fields)
{
    public IReadOnlyDictionary<string, object> Fields { get; } = fields;
    public object? this[string name] => Fields.GetValueOrDefault(name);
}

/// <summary>Arguments of one stage: <c>-name value</c> and <c>name=value</c> are named, the rest positional.</summary>
public sealed record ShellArguments(IReadOnlyDictionary<string, object> Named, IReadOnlyList<object> Positional);

/// <summary>Shell budgets. Fuel is charged per token, per stage and per record a stage touches.</summary>
public sealed record ShellLimits(int Fuel = 4096, int MaxLineLength = 1024, int MaxStages = 12, int MaxRecords = 256,
    int MaxArguments = 16, int MaxActions = 4, int ActionFuel = 64);

public sealed record ShellResult(ShellStatus Status, IReadOnlyList<ShellRecord> Records, int FuelUsed, string? Error = null,
    int Position = 0)
{
    public bool Ok => Status == ShellStatus.Ok;
}

/// <summary>
/// The commands that produce records: what the device running the line can read from the network.
/// A source returns null for a name it does not provide; it must respect <paramref name="maxRecords"/>.
/// </summary>
public interface IShellSource
{
    IReadOnlyList<ShellRecord>? Read(string command, ShellArguments arguments, int maxRecords, out string? error);
}

/// <summary>
/// The commands that act: the last stage of a line, or the only one. An action receives the records the
/// pipeline produced (null when it stands alone) and answers one record per thing it did. The shell admits
/// at most <see cref="ShellLimits.MaxActions"/> input records and charges fuel per record before it runs,
/// so an action never starts on a pipeline that is too wide: it runs for all of its input or not at all.
/// </summary>
public interface IShellSink
{
    bool Knows(string command);
    IReadOnlyList<ShellRecord>? Act(string command, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error);
}

/// <summary>
/// The network shell (§14.6): a line is a pipeline of stages over typed records, in the manner of
/// PowerShell. The first stage is a source command of the device (<c>services</c>, <c>links</c>…), the
/// following ones are the built-in filters <c>where</c>, <c>select</c>, <c>sort</c>, <c>first</c>,
/// <c>last</c> and <c>count</c>. There is no eval, no file access and no loop here: a line always ends,
/// and its cost is bounded by <see cref="ShellLimits"/> before it starts and charged as it runs.
/// </summary>
public static class NetShell
{
    public const int MaxScriptLength = 2048;
    public const int MaxScriptLines = 12;
    public const int MaxConditionDepth = 4;

    private static readonly HashSet<string> Filters = new(StringComparer.Ordinal) { "where", "select", "sort", "first", "last", "count" };
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal) { "eq", "ne", "lt", "le", "gt", "ge", "like" };

    private sealed record Stage(string Command, ShellArguments Arguments, int Position);
    private sealed record Pipeline(Stage First, Stage[] Filters, Stage? Action);
    private sealed record ScriptStep(Pipeline Pipeline, int Offset, bool Condition)
    {
        public int AfterBlock { get; set; }
    }

    public static ShellResult Run(string line, IShellSource source, ShellLimits? limits = null, IShellSink? sink = null)
    {
        limits ??= new ShellLimits();
        var failure = Compile(line, limits, sink, false, 0, out var pipeline, out var fuel);
        var remainingActions = limits.MaxActions;
        return failure ?? (pipeline is null ? new ShellResult(ShellStatus.Ok, [], fuel)
            : Execute(pipeline, source, limits, sink, fuel, ref remainingActions));
    }

    /// <summary>Runs pipelines in order. An if block runs when its read-only pipeline produces records.</summary>
    public static ShellResult RunScript(string script, IShellSource source, ShellLimits? limits = null, IShellSink? sink = null)
    {
        limits ??= new ShellLimits();
        if (script.Length > MaxScriptLength)
            return new(ShellStatus.LimitExceeded, [], 0, "script too long");
        var lines = script.Split('\n');
        if (lines.Length > MaxScriptLines)
            return new(ShellStatus.LimitExceeded, [], 0, $"a script takes at most {MaxScriptLines} lines");

        var steps = new List<ScriptStep>(lines.Length);
        var blocks = new Stack<ScriptStep>();
        var offset = 0;
        var fuel = 0;
        foreach (var line in lines)
        {
            var content = line.TrimStart();
            var start = offset + line.Length - content.Length;
            offset += line.Length + 1;
            if (content.TrimEnd() == "end")
            {
                if (!blocks.TryPop(out var block))
                    return new(ShellStatus.ParseError, [], fuel, "end needs a matching if", start);
                block.AfterBlock = steps.Count;
                continue;
            }
            var condition = content == "if" || (content.StartsWith("if", StringComparison.Ordinal) &&
                content.Length > 2 && char.IsWhiteSpace(content[2]));
            var failure = Compile(condition ? content[2..] : content, limits, sink, condition, fuel, out var pipeline, out fuel);
            var pipelineOffset = start + (condition ? 2 : 0);
            if (failure is not null)
                return failure with { Position = failure.Position + pipelineOffset };
            if (pipeline is null)
            {
                if (condition)
                    return new(ShellStatus.ParseError, [], fuel, "if needs a pipeline", start);
                continue;
            }
            var step = new ScriptStep(pipeline, pipelineOffset, condition);
            if (condition)
            {
                if (blocks.Count == MaxConditionDepth)
                    return new(ShellStatus.LimitExceeded, [], fuel, "too many nested if blocks", start);
                blocks.Push(step);
            }
            steps.Add(step);
        }
        if (blocks.TryPeek(out var unclosed))
            return new(ShellStatus.ParseError, [], fuel, "if needs a matching end", unclosed.Offset - 2);

        IReadOnlyList<ShellRecord> records = [];
        var remainingActions = limits.MaxActions;
        for (var index = 0; index < steps.Count;)
        {
            var step = steps[index];
            var result = Execute(step.Pipeline, source, limits, sink, fuel, ref remainingActions);
            fuel = result.FuelUsed;
            if (!result.Ok)
                return result with { Position = result.Position + step.Offset };
            if (!step.Condition)
                records = result.Records;
            index = step.Condition && result.Records.Count == 0 ? step.AfterBlock : index + 1;
        }
        return new(ShellStatus.Ok, records, fuel);
    }

    private static ShellResult? Compile(string line, ShellLimits limits, IShellSink? sink, bool condition, int initialFuel,
        out Pipeline? pipeline, out int fuelUsed)
    {
        pipeline = null;
        fuelUsed = initialFuel;
        var fuel = initialFuel;
        ShellResult Fail(ShellStatus status, string error, int position = 0) => new(status, [], fuel, error, position);
        if (line.Length > limits.MaxLineLength)
            return Fail(ShellStatus.LimitExceeded, "line too long");
        var parsed = TryParse(line, limits, ref fuel, out var stages, out var parseError, out var parsePosition);
        fuelUsed = fuel;
        if (!parsed)
            return Fail(fuel > limits.Fuel ? ShellStatus.FuelExhausted : ShellStatus.ParseError, parseError!, parsePosition);
        if (stages.Count == 0)
            return null;
        if (stages.Count > limits.MaxStages)
            return Fail(ShellStatus.LimitExceeded, "too many stages");
        if (Filters.Contains(stages[0].Command))
            return Fail(ShellStatus.InvalidArgument, $"'{stages[0].Command}' needs records from a command before it", stages[0].Position);
        // An action is the last stage, or the whole line.
        var action = sink is not null && sink.Knows(stages[^1].Command) ? stages[^1] : null;
        if (action is not null && condition)
            return Fail(ShellStatus.InvalidArgument, "if needs a read-only pipeline", action.Position);
        if (stages.Count > 1 && sink?.Knows(stages[0].Command) == true)
            return Fail(ShellStatus.InvalidArgument, "an action must be last", stages[0].Position);
        var filters = (action is null ? stages.Skip(1) : stages.Skip(1).Take(stages.Count - 2)).ToArray();
        // The whole line is checked before anything is read from the network.
        foreach (var stage in filters)
        {
            if (!Filters.Contains(stage.Command))
                return Fail(ShellStatus.UnknownCommand, $"'{stage.Command}' cannot take records from a pipeline", stage.Position);
            if (Check(stage) is { } invalid)
                return Fail(ShellStatus.InvalidArgument, invalid, stage.Position);
        }
        pipeline = new Pipeline(stages[0], filters, action);
        return null;
    }

    private static ShellResult Execute(Pipeline pipeline, IShellSource source, ShellLimits limits, IShellSink? sink,
        int fuel, ref int remainingActions)
    {
        ShellResult Fail(ShellStatus status, string error, int position = 0) => new(status, [], fuel, error, position);
        IReadOnlyList<ShellRecord>? current = null;
        if (pipeline.First != pipeline.Action)
        {
            var first = pipeline.First;
            current = source.Read(first.Command, first.Arguments, limits.MaxRecords, out var sourceError);
            if (current is null)
                return sourceError is null
                    ? Fail(ShellStatus.UnknownCommand, $"unknown command '{first.Command}'", first.Position)
                    : Fail(ShellStatus.SourceFailed, sourceError, first.Position);
            if (current.Count > limits.MaxRecords)
                return Fail(ShellStatus.LimitExceeded, "too many records", first.Position);
            foreach (var stage in pipeline.Filters)
            {
                fuel += 1 + current.Count;
                if (fuel > limits.Fuel)
                    return Fail(ShellStatus.FuelExhausted, "out of fuel", stage.Position);
                current = Apply(stage, current);
            }
        }
        if (pipeline.Action is { } action)
        {
            var targets = current?.Count ?? 1;
            if (targets > remainingActions)
                return Fail(ShellStatus.LimitExceeded, $"action budget exceeded ({limits.MaxActions} targets total)", action.Position);
            fuel += limits.ActionFuel * targets;
            if (fuel > limits.Fuel)
                return Fail(ShellStatus.FuelExhausted, "out of fuel", action.Position);
            remainingActions -= targets;
            var done = sink!.Act(action.Command, current, action.Arguments, out var actionError);
            return done is null ? Fail(ShellStatus.InvalidArgument, actionError ?? "the action was refused", action.Position)
                : new ShellResult(ShellStatus.Ok, done, fuel);
        }
        return new ShellResult(ShellStatus.Ok, current!, fuel);
    }

    private static string? Check(Stage stage)
    {
        var args = stage.Arguments;
        switch (stage.Command)
        {
            case "where":
                var comparison = args.Named.Keys.FirstOrDefault();
                return args.Positional.Count == 1 && args.Positional[0] is string && args.Named.Count == 1 && Comparisons.Contains(comparison!)
                    ? null : "where <field> -eq|-ne|-lt|-le|-gt|-ge|-like <value>";
            case "select":
                return args.Positional.Count > 0 && args.Named.Count == 0 && args.Positional.All(field => field is string)
                    ? null : "select <field> [<field>…]";
            case "sort":
                return args.Positional.Count == 1 && args.Positional[0] is string &&
                    (args.Named.Count == 0 || (args.Named.Count == 1 && args.Named.TryGetValue("desc", out var flag) && flag is true))
                    ? null : "sort <field> [-desc]";
            case "first" or "last":
                return args.Positional.Count == 1 && args.Positional[0] is long and >= 0 && args.Named.Count == 0
                    ? null : $"{stage.Command} <count>";
            default:
                return args.Positional.Count == 0 && args.Named.Count == 0 ? null : "count takes no arguments";
        }
    }

    private static IReadOnlyList<ShellRecord> Apply(Stage stage, IReadOnlyList<ShellRecord> records)
    {
        var args = stage.Arguments;
        switch (stage.Command)
        {
            case "where":
                var field = (string)args.Positional[0];
                var (comparison, wanted) = args.Named.Single();
                // A record without the field matches nothing, also not -ne: it has no value to compare.
                return records.Where(record => record[field] is { } value && Matches(value, comparison, wanted)).ToArray();
            case "select":
                var fields = args.Positional.Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                return records.Select(record => new ShellRecord(fields.Where(record.Fields.ContainsKey)
                    .ToDictionary(name => name, name => record.Fields[name], StringComparer.Ordinal))).ToArray();
            case "sort":
                var key = (string)args.Positional[0];
                // A stable sort; records without the field come last in either direction.
                var having = records.Where(record => record[key] is not null);
                var ordered = args.Named.ContainsKey("desc")
                    ? having.OrderByDescending(record => record[key]!, ValueOrder.Instance)
                    : having.OrderBy(record => record[key]!, ValueOrder.Instance);
                return ordered.Concat(records.Where(record => record[key] is null)).ToArray();
            case "first":
                return records.Take((int)Math.Min((long)args.Positional[0], int.MaxValue)).ToArray();
            case "last":
                return records.TakeLast((int)Math.Min((long)args.Positional[0], int.MaxValue)).ToArray();
            default:
                return [new ShellRecord(new Dictionary<string, object>(StringComparer.Ordinal) { ["count"] = (long)records.Count })];
        }
    }

    private static bool Matches(object value, string comparison, object wanted)
    {
        if (comparison == "like")
            return value is string text && wanted is string pattern && Like(text, pattern);
        var order = ValueOrder.Compare(value, wanted);
        if (order is null)
            return comparison == "ne"; // Values of different kinds are never equal and have no order.
        return comparison switch
        {
            "eq" => order == 0, "ne" => order != 0, "lt" => order < 0, "le" => order <= 0, "gt" => order > 0, _ => order >= 0
        };
    }

    /// <summary>Text match with <c>*</c> for any run of characters; no backtracking beyond one star at a time.</summary>
    private static bool Like(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
                return false;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private sealed class ValueOrder : IComparer<object>
    {
        public static readonly ValueOrder Instance = new();

        int IComparer<object>.Compare(object? x, object? y) => Compare(x!, y!) ?? string.CompareOrdinal(Kind(x!), Kind(y!));

        private static string Kind(object value) => value is string ? "text" : value is bool ? "flag" : "number";

        /// <summary>Numbers compare as numbers, text ordinally, flags false before true; null for different kinds.</summary>
        public static int? Compare(object left, object right) => (left, right) switch
        {
            (long a, long b) => a.CompareTo(b),
            (long or double, long or double) => Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture)),
            (string a, string b) => string.CompareOrdinal(a, b),
            (bool a, bool b) => a.CompareTo(b),
            _ => null
        };
    }

    // ---- Parsing ---------------------------------------------------------------------------

    private static bool TryParse(string line, ShellLimits limits, ref int fuel, out List<Stage> stages, out string? error, out int position)
    {
        var parsed = new List<Stage>();
        stages = parsed;
        error = null;
        position = 0;
        var index = 0;
        string? command = null;
        var commandAt = 0;
        var named = new Dictionary<string, object>(StringComparer.Ordinal);
        var positional = new List<object>();
        string? pendingName = null;

        bool Close(int at, out string? failure)
        {
            failure = null;
            if (pendingName is not null)
            {
                named[pendingName] = true; // A switch: -desc.
                pendingName = null;
            }
            if (named.Count + positional.Count > limits.MaxArguments)
            {
                failure = "too many arguments";
                return false;
            }
            if (command is null)
            {
                failure = "a stage needs a command";
                return false;
            }
            parsed.Add(new Stage(command, new ShellArguments(named, positional), commandAt));
            command = null;
            named = new Dictionary<string, object>(StringComparer.Ordinal);
            positional = [];
            return true;
        }

        while (true)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;
            if (index >= line.Length)
                break;
            if (++fuel > limits.Fuel)
            {
                error = "out of fuel";
                position = index;
                return false;
            }
            var start = index;
            if (line[index] == '|')
            {
                index++;
                if (!Close(start, out error))
                {
                    position = start;
                    return false;
                }
                // A pipe must be followed by a stage.
                if (line.AsSpan(index).Trim().IsEmpty)
                {
                    error = "a pipe needs a stage after it";
                    position = start;
                    return false;
                }
                continue;
            }
            object value;
            var quoted = line[index] == '"';
            if (quoted)
            {
                var end = line.IndexOf('"', index + 1);
                if (end < 0)
                {
                    error = "unterminated text";
                    position = start;
                    return false;
                }
                value = line[(index + 1)..end];
                index = end + 1;
            }
            else
            {
                while (index < line.Length && !char.IsWhiteSpace(line[index]) && line[index] is not ('|' or '"'))
                    index++;
                var word = line[start..index];
                if (command is null)
                {
                    if (!ServiceNames.IsValid(word) || word[0] == '-')
                    {
                        error = $"'{word}' is not a command name";
                        position = start;
                        return false;
                    }
                    command = word;
                    commandAt = start;
                    continue;
                }
                // -name starts a named argument or a switch; a negative number stays a number.
                if (word.Length > 1 && word[0] == '-' && !char.IsAsciiDigit(word[1]))
                {
                    if (pendingName is not null)
                        named[pendingName] = true;
                    pendingName = word[1..];
                    if (!ServiceNames.IsValid(pendingName) || named.ContainsKey(pendingName))
                    {
                        error = $"'{word}' is not a usable argument name";
                        position = start;
                        return false;
                    }
                    continue;
                }
                var equals = word.IndexOf('=');
                if (equals > 0 && pendingName is null && ServiceNames.IsValid(word[..equals]))
                {
                    if (!named.TryAdd(word[..equals], Scalar(word[(equals + 1)..])))
                    {
                        error = $"'{word[..equals]}' is given twice";
                        position = start;
                        return false;
                    }
                    continue;
                }
                value = Scalar(word);
            }
            if (command is null)
            {
                error = "a stage starts with a command name";
                position = start;
                return false;
            }
            if (pendingName is not null)
            {
                named[pendingName] = value;
                pendingName = null;
            }
            else
                positional.Add(value);
            if (named.Count + positional.Count > limits.MaxArguments)
            {
                error = "too many arguments";
                position = start;
                return false;
            }
        }
        if (command is null && parsed.Count == 0 && pendingName is null)
            return true; // An empty line does nothing.
        if (!Close(line.Length, out error))
        {
            position = line.Length;
            return false;
        }
        return true;
    }

    /// <summary>A bare word is a whole number, a number, a flag or text, in that order.</summary>
    private static object Scalar(string word) =>
        long.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole
        : double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number
        : word is "true" ? true : word is "false" ? false : word;
}

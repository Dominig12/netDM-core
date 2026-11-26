using System.Globalization;
using System.Text;

namespace NetRun.Core;

/// <summary>
/// A program as text a player can keep, paste and share (SCRIPT-003). The format is a small line language
/// that compiles into exactly the same IR the built-in presets use: importing never carries rights, costs,
/// cooldowns, a host identifier from the previous round or a running instance, only the sequence itself.
/// </summary>
public sealed record ProgramSource(string Name, IReadOnlyList<ProgramStep> Steps, int InstructionBudget = 512)
{
    public const int MaxTextLength = 8_192;
    public const int MaxBudget = 4_096;

    private static readonly string[] Statements = ["invoke", "wait", "repeat", "emit", "stop", "budget"];

    /// <summary>
    /// Parses the text form. The result is only a source: starting it still needs an owner and an execution
    /// host supplied by the trusted side, and the definition is validated again by the scheduler.
    /// </summary>
    public static ProgramStatus TryParse(string text, out ProgramSource? source, out string? error)
    {
        source = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty";
            return ProgramStatus.InvalidDefinition;
        }
        if (text.Length > MaxTextLength)
        {
            error = "too_long";
            return ProgramStatus.InvalidDefinition;
        }
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var index = 0;
        var name = "program";
        var budget = 512;
        var steps = new List<ProgramStep>();
        // Frames: (body, remaining repeats, current count)
        var result = ParseBlock(lines, ref index, ref budget, ref name, top: true, steps);
        if (result != ProgramStatus.Ok)
        {
            error = "syntax";
            return result;
        }
        if (steps.Count == 0)
        {
            error = "no_steps";
            return ProgramStatus.InvalidDefinition;
        }
        source = new ProgramSource(name, steps, budget);
        return ProgramStatus.Ok;
    }

    private static ProgramStatus ParseBlock(string[] lines, ref int index, ref int budget, ref string name,
        bool top, List<ProgramStep> into)
    {
        while (index < lines.Length)
        {
            var line = Strip(lines[index++]);
            if (line.Length == 0)
                continue;
            if (line == "}")
            {
                if (top)
                    return ProgramStatus.InvalidDefinition;
                return ProgramStatus.Ok;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0];
            if (keyword == "program" && top)
            {
                name = parts.Length > 1 ? Unquote(line[(line.IndexOf(' ') + 1)..]) : "program";
                if (name.Length is 0 or > 64)
                    return ProgramStatus.InvalidDefinition;
                continue;
            }
            if (keyword == "budget")
            {
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 1 or > MaxBudget)
                    return ProgramStatus.InvalidDefinition;
                budget = parsed;
                continue;
            }
            if (keyword == "repeat")
            {
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                    count is < 1 or > 1_000)
                    return ProgramStatus.InvalidDefinition;
                if (index >= lines.Length || Strip(lines[index]) != "{")
                    return ProgramStatus.InvalidDefinition;
                index++;
                var body = new List<ProgramStep>();
                var nested = ParseBlock(lines, ref index, ref budget, ref name, top: false, body);
                if (nested != ProgramStatus.Ok || body.Count == 0)
                    return ProgramStatus.InvalidDefinition;
                into.Add(new ProgramStep(StepKind.Repeat, Count: count, Body: body));
                continue;
            }
            if (keyword == "invoke")
            {
                if (parts.Length is < 2 or > 3)
                    return ProgramStatus.InvalidDefinition;
                var action = parts[1];
                if (!IsToken(action))
                    return ProgramStatus.InvalidDefinition;
                var target = HostTarget.Self;
                if (parts.Length == 3)
                {
                    var token = parts[2];
                    if (token == "self")
                    {
                        target = HostTarget.Self;
                    }
                    else if (token.StartsWith('#') &&
                             ulong.TryParse(token.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var host) &&
                             host > 0)
                    {
                        target = HostTarget.Of(new HostId(host));
                    }
                    else
                    {
                        return ProgramStatus.InvalidDefinition;
                    }
                }
                into.Add(new ProgramStep(StepKind.Invoke, action, target));
                continue;
            }
            if (keyword == "wait")
            {
                if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wait) ||
                    wait is < 1 or > 300_000)
                    return ProgramStatus.InvalidDefinition;
                into.Add(ProgramStep.Wait(wait));
                continue;
            }
            if (keyword == "emit")
            {
                if (parts.Length != 2 || !IsToken(parts[1]))
                    return ProgramStatus.InvalidDefinition;
                into.Add(ProgramStep.Emit(parts[1]));
                continue;
            }
            if (keyword == "stop")
            {
                into.Add(ProgramStep.Stop());
                continue;
            }
            _ = Statements;
            return ProgramStatus.InvalidDefinition;
        }
        return top ? ProgramStatus.Ok : ProgramStatus.InvalidDefinition;
    }

    /// <summary>Writes the source back in the same format, so an edit round-trips through the text.</summary>
    public string ToText()
    {
        var text = new StringBuilder(256);
        text.Append("program ").Append(Quote(Name)).Append('\n');
        if (InstructionBudget != 512)
            text.Append("  budget ").Append(InstructionBudget.ToString(CultureInfo.InvariantCulture)).Append('\n');
        WriteSteps(text, Steps, 1);
        return text.ToString();
    }

    private static void WriteSteps(StringBuilder text, IReadOnlyList<ProgramStep> steps, int depth)
    {
        var pad = new string(' ', depth * 2);
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case StepKind.Invoke:
                    text.Append(pad).Append("invoke ").Append(step.ActionId).Append(' ').Append(step.Target).Append('\n');
                    break;
                case StepKind.Wait:
                    text.Append(pad).Append("wait ").Append(step.WaitMilliseconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                case StepKind.Repeat:
                    text.Append(pad).Append("repeat ").Append(step.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    text.Append(pad).Append("{\n");
                    WriteSteps(text, step.Body ?? [], depth + 1);
                    text.Append(pad).Append("}\n");
                    break;
                case StepKind.Emit:
                    text.Append(pad).Append("emit ").Append(step.Topic).Append('\n');
                    break;
                default:
                    text.Append(pad).Append("stop").Append('\n');
                    break;
            }
        }
    }

    private static string Strip(string line)
    {
        // Comments are a leading '#' or a trailing //, so "#12" stays a host reference.
        if (line.TrimStart().StartsWith('#'))
            return string.Empty;
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
            line = line[..comment];
        return line.Trim();
    }

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= 96 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return trimmed;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
}

/// <summary>
/// The starting library of §11.5. Each preset is ordinary text, so a player opens it, changes it and keeps
/// the result: presets are not a privileged path into the domain.
/// </summary>
public static class ProgramPresets
{
    public static IReadOnlyList<ProgramSource> All { get; } =
    [
        Source("Scanner", """
            program "Scanner"
              repeat 3
              {
                invoke net.scan self
                wait 2100
              }
              emit program.scan.done
              stop
            """),
        Source("Inspector", """
            program "Inspector"
              invoke net.inspect self
              emit program.inspect.done
              stop
            """),
        Source("Watchdog", """
            program "Watchdog"
              repeat 6
              {
                invoke net.inspect self
                wait 5000
              }
              emit program.watchdog.done
              stop
            """),
        Source("Guardian", """
            program "Guardian"
              repeat 4
              {
                invoke net.scan self
                invoke net.inspect self
                wait 3000
              }
              emit program.guardian.done
              stop
            """),
        Source("Breach", """
            program "Breach"
              invoke security.breach.stealth self
              wait 1000
              invoke security.breach.normal self
              stop
            """),
    ];

    public static ProgramSource? Find(string name)
    {
        foreach (var preset in All)
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
                return preset;
        return null;
    }

    private static ProgramSource Source(string name, string text)
    {
        var status = ProgramSource.TryParse(text, out var source, out var error);
        if (status != ProgramStatus.Ok || source is null)
            throw new InvalidOperationException($"Preset '{name}' does not parse: {error}.");
        return source;
    }
}

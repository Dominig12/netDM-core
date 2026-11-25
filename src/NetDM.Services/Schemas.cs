using System.Buffers;
using System.Text.Json;
using NetDM.Services.Shell;

namespace NetDM.Services;

public enum SchemaFieldType : byte { Integer, Number, Text, Enum, Boolean, MultilineText }

/// <summary>
/// One declared payload field. Values outside the declared range, type or length
/// are rejected; there is no coercion and no implicit unit conversion.
/// </summary>
public sealed record SchemaField(
    string Name, SchemaFieldType Type, bool Required,
    double Min = -9_007_199_254_740_991, double Max = 9_007_199_254_740_991,
    int MaxLength = 0, IReadOnlyList<string>? Values = null, int SinceMinor = 0);

/// <summary>
/// A versioned message contract. Minor versions only add optional fields. A
/// projection is a named subset of fields that a recipient may accept.
/// </summary>
public sealed record SchemaDefinition(
    string Id, int Major, int Minor, IReadOnlyList<SchemaField> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Projections, int MaxPayloadBytes = 1024)
{
    public SchemaField? Field(string name)
    {
        foreach (var field in Fields)
            if (field.Name == name)
                return field;
        return null;
    }
}

/// <summary>A payload that passed validation, in schema field order.</summary>
public sealed class CanonicalPayload
{
    internal CanonicalPayload(SchemaDefinition schema, int minor, IReadOnlyDictionary<string, JsonElement> values, string digest)
    {
        Schema = schema;
        Minor = minor;
        Values = values;
        Digest = digest;
    }

    public SchemaDefinition Schema { get; }
    public int Minor { get; }
    public IReadOnlyDictionary<string, JsonElement> Values { get; }
    /// <summary>Hash of the canonical form; equal content always has an equal digest.</summary>
    public string Digest { get; }
}

/// <summary>
/// A command of a service type: validated arguments in, validated result out.
/// Host-handled commands are answered by the Host on the provider's behalf after
/// the request physically reaches the provider; DM never sees them.
/// </summary>
public sealed record CommandDefinition(string ServiceType, string Name, SchemaDefinition Arguments,
    SchemaDefinition Result, bool HostHandled = false, bool Transient = false, bool ShellReadableResult = false);

public sealed partial class SchemaRegistry
{
    public const int AbsoluteMaxPayloadBytes = 8 * 1024;
    public const int ChatTextMaxLength = 160, ChatSnapshotMaxLength = 4096;
    public const string DescribeCommand = "directory.describe";
    /// <summary>Outcomes of research.server/research, in the wire spelling.</summary>
    public static readonly string[] ResearchOutcomes =
    [
        "researched", "already_researched", "prerequisites_missing", "quote_changed", "insufficient_points",
        "unknown_node", "stale_revision", "storage_unavailable", "catalog_unavailable"
    ];
    /// <summary>A text field's own cap; the payload cap (8 KiB at most) still bounds the message.
    /// A research sync batch carries up to 4000 characters of node IDs.</summary>
    private const int MaxTextLength = 4096;
    private readonly Dictionary<(string Id, int Major), SchemaDefinition> _schemas = new();
    private readonly Dictionary<(string ServiceType, string Name), CommandDefinition> _commands = new();
    private readonly Dictionary<string, List<string>> _commandNames = new(StringComparer.Ordinal);

    public static SchemaRegistry CreateDefault(IEnumerable<IServiceContractModule>? extensions = null)
    {
        var registry = new SchemaRegistry();
        // The stable protocol is registered through the same extension point a game module uses, so the
        // built-in set is not a privileged path (ARCH-002).
        foreach (var module in BuiltinContractModules)
            module.Register(registry);
        // Game-specific modules are added after the stable built-in protocol.
        // Duplicate schemas or commands are rejected by Register/RegisterCommand.
        if (extensions is not null)
        {
            foreach (var extension in extensions)
            {
                ArgumentNullException.ThrowIfNull(extension);
                extension.Register(registry);
            }
        }

        return registry;
    }

    public void RegisterCommand(string serviceType, string name, string argumentsSchema, int argumentsMajor,
        string resultSchema, int resultMajor, bool hostHandled = false, bool transient = false, bool shellReadableResult = false)
    {
        var arguments = Find(argumentsSchema, argumentsMajor);
        var result = Find(resultSchema, resultMajor);
        if (!ServiceNames.IsValid(serviceType) || !ServiceNames.IsValid(name) || name == DescribeCommand ||
            arguments is null || result is null || !arguments.Projections.ContainsKey("full") ||
            !result.Projections.ContainsKey("full"))
            throw new ArgumentException("Invalid command definition.");
        if (!_commands.TryAdd((serviceType, name), new CommandDefinition(serviceType, name, arguments, result, hostHandled, transient, shellReadableResult)))
            throw new ArgumentException($"Command '{serviceType}/{name}' is already registered.");
        if (!_commandNames.TryGetValue(serviceType, out var names))
            _commandNames[serviceType] = names = [];
        names.Add(name);
    }

    public CommandDefinition? FindCommand(string serviceType, string name) =>
        name == DescribeCommand && Find("directory.probe", 1) is { } probe && Find("directory.descriptor", 1) is { } descriptor
            ? new CommandDefinition(serviceType, DescribeCommand, probe, descriptor, HostHandled: true)
            : _commands.GetValueOrDefault((serviceType, name));

    public IReadOnlyList<string> CommandNames(string serviceType) =>
        _commandNames.TryGetValue(serviceType, out var names) ? names : [];

    public void Register(SchemaDefinition schema)
    {
        if (!ServiceNames.IsValid(schema.Id) || schema.Major < 1 || schema.Minor < 0 ||
            schema.MaxPayloadBytes is < 1 or > AbsoluteMaxPayloadBytes || schema.Fields.Count is < 1 or > 64)
            throw new ArgumentException("Invalid schema definition.", nameof(schema));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (!ServiceNames.IsValid(field.Name) || !names.Add(field.Name) || field.SinceMinor > schema.Minor ||
                (field.Required && field.SinceMinor > 0) || field.Min > field.Max ||
                (field.Type is SchemaFieldType.Text or SchemaFieldType.MultilineText && field.MaxLength is < 1 or > MaxTextLength) ||
                (field.Type == SchemaFieldType.Enum && field.Values is not { Count: > 0 }))
                throw new ArgumentException($"Invalid field '{field.Name}' in schema '{schema.Id}'.", nameof(schema));
        }
        foreach (var (name, fields) in schema.Projections)
            if (!ServiceNames.IsValid(name) || fields.Count == 0 || fields.Any(f => !names.Contains(f)))
                throw new ArgumentException($"Invalid projection '{name}' in schema '{schema.Id}'.", nameof(schema));
        if (!_schemas.TryAdd((schema.Id, schema.Major), schema))
            throw new ArgumentException($"Schema '{schema.Id}' v{schema.Major} is already registered.", nameof(schema));
    }

    public SchemaDefinition? Find(string id, int major) =>
        _schemas.TryGetValue((id, major), out var schema) ? schema : null;

    /// <summary>
    /// Validates a producer payload declared at <paramref name="minor"/>. Unknown,
    /// newer-minor, duplicate or out-of-range fields reject the whole payload.
    /// </summary>
    public ServiceStatus Validate(SchemaDefinition schema, int minor, JsonElement payload, out CanonicalPayload? canonical)
    {
        canonical = null;
        if (minor < 0 || minor > schema.Minor || payload.ValueKind != JsonValueKind.Object)
            return ServiceStatus.InvalidPayload;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            var field = schema.Field(property.Name);
            if (field is null || field.SinceMinor > minor || values.ContainsKey(property.Name) ||
                !IsValidValue(field, property.Value))
                return ServiceStatus.InvalidPayload;
            // DM encodes booleans as 0/1; the canonical form always uses true/false.
            values[property.Name] = field.Type == SchemaFieldType.Boolean && property.Value.ValueKind == JsonValueKind.Number
                ? (property.Value.GetInt32() == 1 ? True : False)
                : property.Value.Clone();
        }
        foreach (var field in schema.Fields)
            if (field.Required && !values.ContainsKey(field.Name))
                return ServiceStatus.InvalidPayload;
        var full = Write(schema, values, null);
        if (full.Length > schema.MaxPayloadBytes)
            return ServiceStatus.InvalidPayload;
        canonical = new CanonicalPayload(schema, minor, values,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(full)));
        return ServiceStatus.Ok;
    }

    /// <summary>Serializes the fields of one projection in schema order.</summary>
    public static byte[] Project(CanonicalPayload payload, string projection)
    {
        if (!payload.Schema.Projections.TryGetValue(projection, out var fields))
            throw new ArgumentException("Unknown projection.", nameof(projection));
        return Write(payload.Schema, payload.Values, new HashSet<string>(fields, StringComparer.Ordinal));
    }

    private static byte[] Write(SchemaDefinition schema, IReadOnlyDictionary<string, JsonElement> values,
        HashSet<string>? include)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in schema.Fields)
            {
                if ((include is not null && !include.Contains(field.Name)) || !values.TryGetValue(field.Name, out var value))
                    continue;
                writer.WritePropertyName(field.Name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsValidValue(SchemaField field, JsonElement value) => field.Type switch
    {
        SchemaFieldType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number) && Math.Floor(number) == number && number >= field.Min && number <= field.Max,
        SchemaFieldType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number) && number >= field.Min && number <= field.Max,
        SchemaFieldType.Text => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            text.Length <= field.MaxLength && !text.Any(char.IsControl),
        SchemaFieldType.MultilineText => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            text.Length <= field.MaxLength && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')),
        SchemaFieldType.Enum => value.ValueKind == JsonValueKind.String && field.Values!.Contains(value.GetString()!),
        SchemaFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False ||
            (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var flag) && flag is 0 or 1),
        _ => false
    };

    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();
    private static readonly JsonElement False = JsonDocument.Parse("false").RootElement.Clone();
}

/// <summary>Names of schemas, fields, services, topics and subscriptions.</summary>
public static class ServiceNames
{
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Wire code of an enum value: TransportDelivered → transport_delivered.</summary>
    public static string Code<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && char.IsLower(name[i - 1]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}

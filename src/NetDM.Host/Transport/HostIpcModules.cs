using System.Text.Json;
using NetDM.Host.Research;

namespace NetDM.Host.Transport;

/// <summary>
/// A feature's local control-plane entry points. These commands only coordinate
/// Host state: they do not impersonate network deliveries between DM endpoints.
/// </summary>
public interface IHostIpcModule
{
    IReadOnlyCollection<string> Operations { get; }

    /// <summary>Whether the command requires an active topology, transport workers and fresh heartbeat.</summary>
    bool RequiresNetworkReady { get; }

    HostIpcResult Execute(string operation, JsonElement? payload);
}

public sealed record HostIpcResult(bool Ok, string? Error, object? Value);

/// <summary>
/// Immutable command registry for feature modules. An ambiguous registration is a
/// startup failure, not a silent last-wins replacement.
/// </summary>
public sealed class HostIpcModuleRouter
{
    private readonly IReadOnlyDictionary<string, IHostIpcModule> _modules;

    public HostIpcModuleRouter(IEnumerable<IHostIpcModule> modules)
    {
        var commands = new Dictionary<string, IHostIpcModule>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            foreach (var operation in module.Operations)
            {
                if (string.IsNullOrWhiteSpace(operation) || !commands.TryAdd(operation, module))
                    throw new InvalidOperationException(
                        $"Duplicate or invalid Host IPC command '{operation}' ({module.GetType().Name}).");
            }
        }
        _modules = commands;
    }

    public bool TryGet(string operation, out IHostIpcModule module) =>
        _modules.TryGetValue(operation, out module!);
}

/// <summary>Research ledger control traffic: catalog, storage and incomes are not game-to-game bypasses.</summary>
internal sealed class ResearchIpcModule(ResearchHost research) : IHostIpcModule
{
    public IReadOnlyCollection<string> Operations { get; } =
    [
        "research_catalog", "research_storage", "research_income", "research_grant",
        "research_debit", "research_import", "research_boost", "research_state",
        "research_replicate", "research_effects", "research_knowledge"
    ];

    // Research startup and journal recovery have to work while the snapshot is being synchronized.
    public bool RequiresNetworkReady => false;

    public HostIpcResult Execute(string operation, JsonElement? payload)
    {
        var (ok, error, result) = research.Handle(operation, payload);
        return new(ok, error, result);
    }
}

internal sealed class RadioReachIpcModule(GameNetworkService game) : IHostIpcModule
{
    public IReadOnlyCollection<string> Operations { get; } = ["radio_reach"];
    public bool RequiresNetworkReady => true;

    public HostIpcResult Execute(string operation, JsonElement? payload)
    {
        var (ok, error, result) = RadioReach.Handle(game.Snapshot, payload);
        return new(ok, error, result);
    }
}

/// <summary>Administrative lifecycle of network entities, not a user endpoint command.</summary>
internal sealed class NetworkLifeIpcModule(GameNetworkService game) : IHostIpcModule
{
    public IReadOnlyCollection<string> Operations { get; } = ["network_life"];
    public bool RequiresNetworkReady => false;

    public HostIpcResult Execute(string operation, JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new(false, "invalid_request", null);
        var (ok, error, result) = Diagnostics.NetworkSpace.Control(game, body);
        return new(ok, error, result);
    }
}

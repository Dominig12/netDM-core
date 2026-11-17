using System.Text.Json;
using NetDM.Services;

namespace NetDM.Host.Transport;

/// <summary>
/// A handler for commands that have completed the route to a Host-provided
/// service endpoint. Registration supplies both the service type and the
/// dispatch implementation, preventing different lists from drifting apart.
/// </summary>
public interface IHostServiceExtension
{
    string ServiceType { get; }

    /// <summary>
    /// Builds the reply for a delivered command; an optional side effect is
    /// committed only after the result has been acknowledged by ServiceRuntime.
    /// </summary>
    JsonElement Answer(GameNetworkService game, HostCommand command, out Action? onAccepted);
}

internal sealed class DiagnosticsHostService : IHostServiceExtension
{
    public string ServiceType => Diagnostics.DiagnosticsHost.ServiceType;

    public JsonElement Answer(GameNetworkService game, HostCommand command, out Action? onAccepted)
    {
        onAccepted = null;
        return Diagnostics.DiagnosticsHost.Answer(game, command);
    }
}

internal sealed class QueryHostService : IHostServiceExtension
{
    public string ServiceType => Diagnostics.QueryHost.ServiceType;

    public JsonElement Answer(GameNetworkService game, HostCommand command, out Action? onAccepted)
    {
        onAccepted = null;
        return Diagnostics.QueryHost.Answer(game.CaptureSession(), command);
    }
}

internal sealed class ShellHostService : IHostServiceExtension
{
    public string ServiceType => Diagnostics.ShellHost.ServiceType;

    public JsonElement Answer(GameNetworkService game, HostCommand command, out Action? onAccepted) =>
        Diagnostics.ShellHost.Answer(game, game.CaptureSession(), command, out onAccepted);
}

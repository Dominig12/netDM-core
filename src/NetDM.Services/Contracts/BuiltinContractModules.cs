namespace NetDM.Services;

/// <summary>
/// The built-in contracts, expressed as modules like any other feature. This is what makes the extension
/// point real: the stable protocol is not a special case compiled into the registry, it is the first set of
/// registrations, and a game module added at composition behaves exactly the same way (ARCH-002).
/// </summary>
public sealed partial class SchemaRegistry
{
    /// <summary>One module per domain, in the order the stable protocol has always been registered in.</summary>
    public static IReadOnlyList<IServiceContractModule> BuiltinContractModules { get; } =
    [
        new AtmosModule(),
        new MachinesModule(),
        new CamerasModule(),
        new ResearchModule(),
        new MessengerModule(),
        new RadioModule(),
        new DiagnosticsModule(),
        new QueriesModule(),
        new ShellModule(),
        new DirectoryModule()
    ];

    private sealed class AtmosModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterAtmos(registry);
    }

    private sealed class MachinesModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterMachines(registry);
    }

    private sealed class CamerasModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterCameras(registry);
    }

    private sealed class ResearchModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterResearch(registry);
    }

    private sealed class MessengerModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterMessenger(registry);
    }

    private sealed class RadioModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterRadio(registry);
    }

    private sealed class DiagnosticsModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterDiagnostics(registry);
    }

    private sealed class QueriesModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterQueries(registry);
    }

    private sealed class ShellModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterShell(registry);
    }

    private sealed class DirectoryModule : IServiceContractModule
    {
        public void Register(SchemaRegistry registry) => RegisterDirectory(registry);
    }
}

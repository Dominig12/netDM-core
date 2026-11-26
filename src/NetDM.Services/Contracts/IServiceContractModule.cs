namespace NetDM.Services;

/// <summary>
/// A feature owns the payload schemas and commands it introduces. Implementations
/// register their contracts once at application composition, rather than editing
/// a central switch or the built-in schema catalog.
/// </summary>
public interface IServiceContractModule
{
    void Register(SchemaRegistry registry);
}

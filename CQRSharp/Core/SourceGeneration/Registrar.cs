namespace CQRSharp.Core.SourceGeneration;

/// <summary>
///     Provides static access to generated registrars for various subsystems
///     (e.g. handler registration, pipeline registration) within the CQRSharp library.
///     The source generators will assign generated instances to these fields automatically.
/// </summary>
public static class Registrar
{
    /// <summary>
    ///     Gets or sets the generated data registrar responsible for handler registration.
    ///     The source generator sets this value via a module initializer.
    /// </summary>
    public static IDataRegistrar? HandlerRegistrar { get; set; }

    /// <summary>
    ///     Gets or sets the generated data registrar responsible for pipeline registration.
    ///     The source generator sets this value via a module initializer.
    /// </summary>
    public static IDataRegistrar? PipelineRegistryRegistrar { get; set; }

    /// <summary>
    ///     Gets or sets the generated data registrar responsible for request registry registration.
    ///     The source generator sets this value via a module initializer.
    /// </summary>
    public static IDataRegistrar? RequestRegistryRegistrar { get; set; }

    /// <summary>
    ///     Represents the data registrar responsible for managing the registry for handlers.
    ///     The source generator sets this value via a module initializer.
    /// </summary>
    public static IDataRegistrar? HandlerRegistryRegistrar { get; set; }

    /// <summary>
    ///     Represents the data registrar responsible for managing the registry that maps all requests to their respective
    ///     context factories.
    ///     The source generator sets this value via a module initializer.
    /// </summary>
    public static IDataRegistrar? ContextFactoryRegistryRegistrar { get; set; }
}
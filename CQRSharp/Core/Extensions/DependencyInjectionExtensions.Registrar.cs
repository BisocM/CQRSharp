using CQRSharp.Core.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Extensions;

public static partial class DependencyInjectionExtensions
{
    /// <summary>
    /// An extension method that uses the <see cref="Registrar"/> to register handlers to the <see cref="IServiceCollection"/>.
    /// The implemented logic for handler registration is sourced from the generated code.
    /// </summary>
    /// <param name="services">The service collection to which the handlers will be registered.</param>
    private static void AddGeneratedHandlers(this IServiceCollection services) => Registrar.HandlerRegistrar?.RegisterData(services);
    
    /// <summary>
    /// An extension method that uses the <see cref="Registrar"/> to register the IRequestRegistry within the DI container.
    /// All implementation logic for the IRequestRegistry is within the source generator code.
    /// </summary>
    /// <param name="services">The service collection to which the handlers will be registered.</param>
    private static void AddGeneratedRequestRegistry(this IServiceCollection services) => Registrar.RequestRegistryRegistrar?.RegisterData(services);

    /// <summary>
    /// An extension method that uses the <see cref="Registrar"/> to register handler registries to the <see cref="IServiceCollection"/>.
    /// The implemented logic for registry registration is sourced from the generated code.
    /// </summary>
    /// <param name="services">The service collection to which the handler registries will be registered.</param>
    private static void AddGeneratedHandlerRegistry(this IServiceCollection services) => Registrar.HandlerRegistryRegistrar?.RegisterData(services);
    
    /// <summary>
    /// Registers the pipeline registry using generated pipeline builders.
    /// Looks for a generated type in the known namespace and uses it if available.
    /// </summary>
    /// <param name="services">The service collection to which the handler registries will be registered.</param>
    private static void AddGeneratedPipelineRegistry(this IServiceCollection services) => Registrar.PipelineRegistryRegistrar?.RegisterData(services);
}
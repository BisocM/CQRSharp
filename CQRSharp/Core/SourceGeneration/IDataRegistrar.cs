using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.SourceGeneration
{
    /// <summary>
    /// Defines a contract for a data registrar that registers various handlers,
    /// pipelines, or other constructs with a dependency injection container.
    /// The CQRSharp library uses this interface to hook into the generated registration
    /// logic without having to implement any runtime discovery.
    /// </summary>
    public interface IDataRegistrar
    {
        /// <summary>
        /// Registers data, such as handlers or pipeline components, with the specified <see cref="IServiceCollection"/>.
        /// The implementation is provided by the source generator.
        /// </summary>
        /// <param name="services">The service collection where registrations should be added.</param>
        void RegisterData(IServiceCollection services);
    }
}
using System.Collections.Concurrent;

namespace CQRSharp.Core.Caching.Contexts;

/// <summary>
///     Provides a concrete implementation of <see cref="IContextFactoryRegistry" />.
///     This registry maps each request context type to a delegate that resolves the appropriate context factory from the
///     DI container.
/// </summary>
public sealed class ContextFactoryRegistry : IContextFactoryRegistry
{
    // Thread-safe dictionary for holding context-to-factory resolver mappings.
    private readonly ConcurrentDictionary<Type, Func<IServiceProvider, object?>> _factoryMappings;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ContextFactoryRegistry" /> class with the specified factory mappings.
    /// </summary>
    /// <param name="factoryMappings">
    ///     A dictionary mapping request context types to a lambda that resolves the corresponding
    ///     <c>IRequestContextFactory&lt;TContext&gt;</c>
    ///     from an <see cref="IServiceProvider" />.
    /// </param>
    public ContextFactoryRegistry(ConcurrentDictionary<Type, Func<IServiceProvider, object?>> factoryMappings)
    {
        _factoryMappings = factoryMappings;
    }

    /// <inheritdoc />
    public object? TryGetFactory(Type contextType, IServiceProvider serviceProvider)
    {
        return _factoryMappings.TryGetValue(contextType, out var resolver)
            ?
            //Resolve and return the factory instance from the DI container.
            resolver(serviceProvider)
            : null;
    }
}

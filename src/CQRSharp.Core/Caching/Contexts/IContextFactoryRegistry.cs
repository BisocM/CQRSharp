namespace CQRSharp.Core.Caching.Contexts;

/// <summary>
///     Defines a registry for mapping request context types to their corresponding context factory resolvers.
/// </summary>
public interface IContextFactoryRegistry
{
    /// <summary>
    ///     Attempts to retrieve an instance of <c>IRequestContextFactory&lt;TContext&gt;</c>
    ///     from the registry based on the specified context type.
    /// </summary>
    /// <param name="contextType">The request context type for which to retrieve a factory.</param>
    /// <param name="serviceProvider">The DI service provider used to resolve the factory.</param>
    /// <returns>
    ///     An instance of the corresponding context factory if one is registered; otherwise, null.
    /// </returns>
    object? TryGetFactory(Type contextType, IServiceProvider serviceProvider);

    /// <summary>
    ///     Returns the resolver for <paramref name="contextType" /> so a caller can look it up once and reuse it, instead
    ///     of paying a registry lookup on every request. <c>null</c> when no factory is registered for the type.
    /// </summary>
    Func<IServiceProvider, object?>? TryGetResolver(Type contextType)
        => serviceProvider => TryGetFactory(contextType, serviceProvider);
}
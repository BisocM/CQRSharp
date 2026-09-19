using System.Collections.Concurrent;

namespace CQRSharp.Core.Caching.Handlers;

/// <summary>
///     The default <see cref="IHandlerRegistry" />: a lookup over the invokers merged from every registered module.
/// </summary>
/// <param name="invokers">The typed handler invokers, keyed by request type.</param>
public class HandlerRegistry(ConcurrentDictionary<Type, Delegate> invokers) : IHandlerRegistry
{
    /// <inheritdoc />
    public Delegate? TryGetInvoker(Type requestType)
        => invokers.TryGetValue(requestType, out var invoker) ? invoker : null;
}

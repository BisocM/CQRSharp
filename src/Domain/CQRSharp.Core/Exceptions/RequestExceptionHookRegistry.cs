using System.Collections.Concurrent;

namespace CQRSharp.Core.Exceptions;

/// <summary>
///     Default <see cref="IRequestExceptionHookRegistry" /> implementation backed by a thread-safe map of
///     request type to its compiled <see cref="RequestExceptionHookInvoker" />. The map is normally populated
///     by the CQRSharp source generator.
/// </summary>
public sealed class RequestExceptionHookRegistry : IRequestExceptionHookRegistry
{
    private readonly ConcurrentDictionary<Type, RequestExceptionHookInvoker> _invokers;

    /// <summary>
    ///     Creates an empty registry backed by a fresh, empty invoker map.
    /// </summary>
    public RequestExceptionHookRegistry()
        : this(new ConcurrentDictionary<Type, RequestExceptionHookInvoker>())
    {
    }

    /// <summary>
    ///     Creates a registry that wraps the supplied invoker map.
    /// </summary>
    /// <param name="invokers">The request-type to exception-hook-invoker map to back this registry.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="invokers" /> is <see langword="null" />.</exception>
    public RequestExceptionHookRegistry(ConcurrentDictionary<Type, RequestExceptionHookInvoker> invokers)
    {
        _invokers = invokers ?? throw new ArgumentNullException(nameof(invokers));
    }

    /// <summary>
    ///     Attempts to retrieve the exception hook invoker registered for the given request type.
    /// </summary>
    /// <param name="requestType">The request type to look up.</param>
    /// <param name="invoker">When this method returns <see langword="true" />, the invoker registered for the request type.</param>
    /// <returns><see langword="true" /> if an invoker is registered for the request type; otherwise <see langword="false" />.</returns>
    public bool TryGetInvoker(Type requestType, out RequestExceptionHookInvoker invoker)
        => _invokers.TryGetValue(requestType, out invoker!);
}


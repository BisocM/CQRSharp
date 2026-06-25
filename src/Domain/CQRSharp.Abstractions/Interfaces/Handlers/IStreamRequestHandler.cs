#if !NETSTANDARD2_0
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;

namespace CQRSharp.Abstractions.Interfaces.Handlers;

/// <summary>
///     Interface for handling streaming requests that yield elements of type <typeparamref name="TItem" />.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The streamed element type.</typeparam>
/// <typeparam name="TContext">The request context type.</typeparam>
public interface IStreamRequestHandler<in TRequest, TItem, TContext>
    where TRequest : IStreamRequest<TItem>
    where TContext : IRequestContext
{
    /// <summary>
    ///     Handles the request and returns an <see cref="IAsyncEnumerable{T}" /> representing the stream.
    /// </summary>
    IAsyncEnumerable<TItem> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
///     Interface for handling streaming requests using the default <see cref="RequestContextBase" /> context.
/// </summary>
public interface IStreamRequestHandler<in TRequest, TItem>
    : IStreamRequestHandler<TRequest, TItem, RequestContextBase>
    where TRequest : IStreamRequest<TItem>;
#endif

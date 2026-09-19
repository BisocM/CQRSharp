using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Interfaces.Markers.Stream;

/// <summary>
///     Non-generic marker interface for streaming requests, used to identify any streaming request
///     regardless of the type of items it yields.
/// </summary>
public interface IStreamRequest : IRequest;

/// <summary>
///     Marker interface for streaming requests that yield items of type <typeparamref name="TItem" />.
/// </summary>
/// <typeparam name="TItem">The type of the items produced by the asynchronous stream.</typeparam>
public interface IStreamRequest<out TItem> : IStreamRequest, IRequest<IAsyncEnumerable<TItem>>;
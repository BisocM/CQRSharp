#if !NETSTANDARD2_0
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;

/// <summary>
///     Marker interface for streaming requests that yield items of type <typeparamref name="TItem" />.
/// </summary>
public interface IStreamRequest : IRequest;

/// <summary>
///     Marker interface for streaming requests that yield items of type <typeparamref name="TItem" />.
/// </summary>
public interface IStreamRequest<out TItem> : IStreamRequest, IRequest<IAsyncEnumerable<TItem>>;
#endif

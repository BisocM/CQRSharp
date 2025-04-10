using CQRSharp.Shared.Data.Attributes.Requests;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;

namespace CQRSharp.Shared.Data.Interfaces.Markers.Query;

/// <summary>
///     Marker interface for commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TResult">The type of the result returned by the query.</typeparam>
[RequestMarker(RequestKind.Query)]
public interface IQuery<TResult> : IRequest;
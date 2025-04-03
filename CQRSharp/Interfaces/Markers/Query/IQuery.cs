using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Interfaces.Markers.Query;

/// <summary>
///     Marker interface for commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TResult">The type of the result returned by the query.</typeparam>
public interface IQuery<TResult> : IRequest;
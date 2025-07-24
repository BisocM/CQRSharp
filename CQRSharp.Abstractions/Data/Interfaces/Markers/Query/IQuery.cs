using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Data.Interfaces.Markers.Query;

/// <summary>
///     Marker interface for commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TResult">The type of the result returned by the query.</typeparam>
public interface IQuery<out TResult> : IRequest;
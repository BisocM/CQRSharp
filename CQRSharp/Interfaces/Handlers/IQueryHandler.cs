using CQRSharp.Interfaces.Markers.Query;

namespace CQRSharp.Interfaces.Handlers;

/// <summary>
///     Interface for handling commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TQuery">The type of the command.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}
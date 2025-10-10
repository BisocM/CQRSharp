using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;

namespace CQRSharp.Abstractions.Data.Interfaces.Handlers;

/// <summary>
///     Interface for handling commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TQuery">The type of the command.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
/// <typeparam name="TContext">The type of the context object carried by the query.</typeparam>
public interface IQueryHandler<in TQuery, TResult, TContext>
    where TQuery : IQuery<TResult>
    where TContext : IRequestContext
{
    /// <summary>
    ///     Processes a given query and returns the result asynchronously.
    /// </summary>
    /// <param name="query">The query object containing the data to be processed.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, containing the result of type <typeparamref name="TResult" />.</returns>
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
///     Interface for handling commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TQuery">The type of the command.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
public interface IQueryHandler<in TQuery, TResult> : IQueryHandler<TQuery, TResult, RequestContextBase>
    where TQuery : IQuery<TResult>;
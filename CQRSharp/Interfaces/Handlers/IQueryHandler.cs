using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Shared.Attributes;
using CQRSharp.Shared.Attributes.Requests;

namespace CQRSharp.Interfaces.Handlers;

/// <summary>
///     Interface for handling commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TQuery">The type of the command.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
/// <typeparam name="TContext">The type of the context object carried by the query.</typeparam>
[HandlerType(HandlerKind.Query)]
public interface IQueryHandler<in TQuery, TResult, TContext>
    where TQuery : IQuery<TResult>
    where TContext : IRequestContext
{
    /// <summary>
    /// Processes a query and returns a result of type <typeparamref name="TResult" />.
    /// </summary>
    /// <typeparam name="TQuery">The type of the query being handled.</typeparam>
    /// <typeparam name="TResult">The type of the result returned by the query.</typeparam>
    /// <typeparam name="TContext">The type of the context associated with the query.</typeparam>
    /// <param name="query">The query to be processed.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task representing the result of processing the query.</returns>
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
///     Interface for handling commands that return a result of type <typeparamref name="TResult" />.
/// </summary>
/// <typeparam name="TQuery">The type of the command.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
[HandlerType(HandlerKind.Query)]
public interface IQueryHandler<in TQuery, TResult> : IQueryHandler<TQuery, TResult, RequestContextBase> where TQuery : IQuery<TResult>;
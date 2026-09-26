namespace CQRSharp;

/// <summary>
///     Handles a query that returns a result of type <typeparamref name="TResult" />. The query's typed context, if it
///     declares one, is on <c>query.Context</c> (see <see cref="RequestBase{TContext}" />).
/// </summary>
/// <typeparam name="TQuery">The type of the query.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the query.</typeparam>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    /// <summary>
    ///     Processes a given query and returns the result asynchronously.
    /// </summary>
    /// <param name="query">The query object containing the data to be processed.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, containing the result of type <typeparamref name="TResult" />.</returns>
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

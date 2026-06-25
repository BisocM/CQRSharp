using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Models.Commands;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Defines the central executor for processing requests through their corresponding pipelines.
///     This is the primary entry point for sending commands and queries.
/// </summary>
public interface IPipelineExecutor
{
    /// <summary>
    ///     Executes a query through its configured pipeline and returns the result.
    /// </summary>
    /// <typeparam name="TRequest">The type of the query, which must implement <see cref="IQuery{TResult}" />.</typeparam>
    /// <typeparam name="TResult">The type of the result expected from the query.</typeparam>
    /// <param name="query">The query object.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous execution operation, containing the query result.</returns>
    Task<TResult> ExecuteQueryAsync<TRequest, TResult>(TRequest query, CancellationToken cancellationToken)
        where TRequest : IQuery<TResult>;

    /// <summary>
    ///     Executes a command through its configured pipeline and returns the result.
    /// </summary>
    /// <typeparam name="TRequest">The type of the command, which must implement <see cref="ICommand" />.</typeparam>
    /// <param name="command">The command object.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous execution operation, containing the <see cref="CommandResult" />.</returns>
    Task<CommandResult> ExecuteCommandAsync<TRequest>(TRequest command, CancellationToken cancellationToken)
        where TRequest : ICommand;

    /// <summary>
    ///     Executes a streaming request through its configured stream pipeline and returns the stream.
    /// </summary>
    /// <typeparam name="TRequest">The streaming request type.</typeparam>
    /// <typeparam name="TItem">The streamed element type.</typeparam>
    IAsyncEnumerable<TItem> ExecuteStreamAsync<TRequest, TItem>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>;
}

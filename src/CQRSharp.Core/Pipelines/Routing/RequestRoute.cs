using System.ComponentModel;
using CQRSharp.Core.Diagnostics;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     How one command or query type is dispatched and described: the executor's generic entry point closed over the
///     request and result types. A source-generated module creates one per request its assembly handles, through
///     <see cref="Command{TRequest}" /> or <see cref="Query{TRequest, TResult}" />; the composition merges every module's
///     routes into one table, so a dispatch is one lookup by the request's exact runtime type. Everything a route does is
///     implemented here, so a module compiled against an earlier 5.x picks up what later versions add to it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class RequestRoute
{
    private protected RequestRoute()
    {
    }

    /// <summary>The route of a command, dispatched with a <see cref="CommandResult" />.</summary>
    /// <typeparam name="TRequest">The command type.</typeparam>
    public static RequestRoute Command<TRequest>() where TRequest : ICommand => CommandRoute<TRequest>.Instance;

    /// <summary>The route of a query, or of a value-returning command dispatched with a <c>CommandResult&lt;T&gt;</c>.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResult">The type the request is dispatched with.</typeparam>
    public static RequestRoute Query<TRequest, TResult>() where TRequest : IRequest<TResult> => QueryRoute<TRequest, TResult>.Instance;

    /// <summary>The type the request is dispatched with.</summary>
    internal abstract Type ResultType { get; }

    /// <summary>Dispatches the request; the task is a <c>Task&lt;TResult&gt;</c> of <see cref="ResultType" />.</summary>
    internal abstract Task Execute(PipelineExecutor executor, IRequest request, CancellationToken cancellationToken);

    /// <summary>Dispatches the request and boxes its result, for the untyped <c>Send(object)</c> path.</summary>
    internal abstract Task<object?> ExecuteBoxed(PipelineExecutor executor, IRequest request, CancellationToken cancellationToken);

    /// <summary>Describes how the request is wired in the describer's scope.</summary>
    internal abstract CqrsRequestBinding Describe(RequestBindingDescriber describer);

    private sealed class CommandRoute<TRequest> : RequestRoute where TRequest : ICommand
    {
        public static readonly CommandRoute<TRequest> Instance = new();

        internal override Type ResultType => typeof(CommandResult);

        internal override Task Execute(PipelineExecutor executor, IRequest request, CancellationToken cancellationToken)
            => executor.ExecuteCommandAsync((TRequest)request, cancellationToken);

        internal override async Task<object?> ExecuteBoxed(PipelineExecutor executor, IRequest request, CancellationToken cancellationToken)
            => await executor.ExecuteCommandAsync((TRequest)request, cancellationToken).ConfigureAwait(false);

        internal override CqrsRequestBinding Describe(RequestBindingDescriber describer)
            => describer.Describe<TRequest, CommandResult>();
    }

    private sealed class QueryRoute<TRequest, TResult> : RequestRoute where TRequest : IRequest<TResult>
    {
        public static readonly QueryRoute<TRequest, TResult> Instance = new();

        internal override Type ResultType => typeof(TResult);

        internal override Task Execute(PipelineExecutor executor, IRequest request, CancellationToken cancellationToken)
            => executor.ExecuteQueryAsync<TRequest, TResult>((TRequest)request, cancellationToken);

        internal override async Task<object?> ExecuteBoxed(PipelineExecutor executor, IRequest request, CancellationToken cancellationToken)
            => await executor.ExecuteQueryAsync<TRequest, TResult>((TRequest)request, cancellationToken).ConfigureAwait(false);

        internal override CqrsRequestBinding Describe(RequestBindingDescriber describer)
            => describer.Describe<TRequest, TResult>();
    }
}

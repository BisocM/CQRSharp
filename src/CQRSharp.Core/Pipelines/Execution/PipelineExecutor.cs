using System.Diagnostics;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Orchestrates the execution of commands and queries through a series of behaviors (the pipeline).
///     This is the central component that connects a request to its handler, wrapping it with cross-cutting concerns
///     like logging, transactions, validation, etc.
/// </summary>
/// <remarks>
///     <para>
///         A request's context is its caller's: it is built from the scope this executor belongs to (the scope of the
///         dispatcher the request was sent through), on the flow that sent the request, before the request is handed to
///         the queue or to a scope of its own. The request then runs where <see cref="RunMode" /> (commands and queries
///         only) and <see cref="ExecutionScopeMode" /> put it, with that context already set.
///     </para>
///     <para>
///         Split across partial files: this one holds the entry points, run-mode/scope routing and request-context
///         initialization; <c>PipelineExecutor.Pipeline.cs</c> the behavior chain and the final handler action with its
///         lifecycle; <c>PipelineExecutor.Streaming.cs</c> the streaming equivalents; <c>PipelineExecutor.Interceptors.cs</c>
///         the post-handler fan-out and the terminal failure notification, which both paths share; and
///         <c>PipelineExecutor.Ordering.cs</c> the priority comparers.
///     </para>
/// </remarks>
internal sealed partial class PipelineExecutor
{
    private const string CommandActivityName = "CQRS Command";
    private const string QueryActivityName = "CQRS Query";
    private const string StreamActivityName = "CQRS Stream";

    private readonly IBackgroundTaskManager _backgroundTaskManager;
    private readonly IOptions<DispatcherOptions> _dispatcherOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _services;
    private readonly RequestPlanCache _plans;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CqrsMetrics _metrics;
    private readonly ConsumerReadiness _consumerReadiness;
    private readonly TimeSpan _consumerStartTimeout;

    // Resolved once: with the outbox disabled (the default) the request path never touches the outbox services.
    private readonly bool _outboxEnabled;

    // An executor is created per scope (per web request), so everything that is the same for every scope arrives
    // pre-resolved in one singleton instead of being looked up again each time.
    internal PipelineExecutor(IServiceProvider serviceProvider, PipelineExecutorShared shared)
    {
        _services = serviceProvider;
        _backgroundTaskManager = shared.BackgroundTaskManager;
        _dispatcherOptions = shared.DispatcherOptions;
        _scopeFactory = shared.ScopeFactory;
        _plans = shared.Plans;
        _outboxEnabled = shared.OutboxEnabled;
        _timeProvider = shared.TimeProvider;
        _logger = shared.Logger;
        _metrics = shared.Metrics;
        _consumerReadiness = shared.ConsumerReadiness;
        _consumerStartTimeout = shared.ConsumerStartTimeout;
    }

    internal static bool IsOutboxEnabled(IOptions<OutboxOptions>? options)
        => options?.Value.Mode is OutboxMode.Enabled or OutboxMode.Transactional;

    /// <summary>
    ///     Executes a query. When <see cref="RunMode.Queued" /> is configured the work is handed to the background
    ///     task queue and the returned task completes when the queued work finishes; otherwise it runs inline. In both
    ///     cases the request context is built from the caller first, then the behavior pipeline is chained and the final
    ///     query handler is invoked.
    /// </summary>
    /// <typeparam name="TRequest">The type of the query, which must implement <see cref="IQuery{TResult}" />.</typeparam>
    /// <typeparam name="TResult">The expected result type of the query.</typeparam>
    /// <param name="query">The query object to be executed.</param>
    /// <param name="ct">A cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing the result of the query.</returns>
    public Task<TResult> ExecuteQueryAsync<TRequest, TResult>(
        TRequest query, CancellationToken ct)
        where TRequest : IRequest<TResult>
    {
        // A value-returning command (ICommand<T>) dispatches through this entry point with a CommandResult<T> result;
        // it is still a command to traces and metrics.
        var activityName = RequestKindOf<TRequest, TResult>.Value == RequestKind.Command ? CommandActivityName : QueryActivityName;
        return ExecuteAsync<TRequest, TResult>(activityName, query, ct);
    }

    /// <summary>
    ///     Executes a command. When <see cref="RunMode.Queued" /> is configured the work is handed to the background
    ///     task queue and the returned task completes when the queued work finishes; otherwise it runs inline. In both
    ///     cases the request context is built from the caller first, then the behavior pipeline is chained and the final
    ///     command handler is invoked.
    /// </summary>
    /// <typeparam name="TRequest">The type of the command, which must implement <see cref="ICommand" />.</typeparam>
    /// <param name="command">The command object to be executed.</param>
    /// <param name="ct">A cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing the <see cref="CommandResult" />.</returns>
    public Task<CommandResult> ExecuteCommandAsync<TRequest>(
        TRequest command, CancellationToken ct)
        where TRequest : ICommand
    {
        return ExecuteAsync<TRequest, CommandResult>(CommandActivityName, command, ct);
    }

    /// <summary>
    ///     Executes a streaming request through its stream pipeline. The stream runs on the flow that enumerates it, in
    ///     the scope <see cref="ExecutionScopeMode" /> gives it.
    /// </summary>
    public IAsyncEnumerable<TItem> ExecuteStreamAsync<TRequest, TItem>(TRequest request, CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        ArgumentNullException.ThrowIfNull(request);

        // RunMode does not apply to a stream: its consumer pulls each item as it goes, so there is no work a queue could
        // run to completion on its own. It runs on the flow that enumerates it, in the scope ScopeMode gives it.
        if (_dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New)
            return ExecuteStreamInProviderCore<TRequest, TItem>(request, inOwnScope: true, ct);

        return TryExecuteBareStream<TRequest, TItem>(request, ct)
               ?? ExecuteStreamInProviderCore<TRequest, TItem>(request, inOwnScope: false, ct);
    }

    // Commands and queries share one execution path; only the activity name differs.
    private Task<TResult> ExecuteAsync<TRequest, TResult>(string activityName, TRequest request, CancellationToken ct)
        where TRequest : IRequest
    {
        if (_dispatcherOptions.Value.RunMode == RunMode.Queued)
        {
            // A request sent from inside any work item of this queue (a queued handler dispatching another request, or
            // work enqueued through IBackgroundTaskManager; awaited or fired and forgotten) runs at once instead of being
            // queued: queued behind its caller, it would park the caller on a consumer slot while it waits for one of its
            // own, and once every slot is held that way every caller waits until shutdown. It still gets a scope of its
            // own, exactly as the queue would have given it, so it neither shares the caller's unit of work nor outlives
            // the caller's scope when fired and forgotten.
            return BackgroundTaskQueueConsumer.IsRunningWorkItemOf(_backgroundTaskManager)
                ? ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, contextCaptured: false, ct)
                : ExecuteQueuedAsync<TRequest, TResult>(activityName, request, ct);
        }

        return _dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New
            ? ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, contextCaptured: false, ct)
            : ExecuteInProviderAsync<TRequest, TResult>(activityName, request, _services, contextCaptured: false, ct);
    }

    private async Task<TResult> ExecuteQueuedAsync<TRequest, TResult>(string activityName, TRequest request, CancellationToken ct)
        where TRequest : IRequest
    {
        // The context is captured here, before the hand-off: the consumer that runs the request sees neither the caller's
        // scope nor its ambient state (the current HTTP user and every other AsyncLocal), and the caller's scope may be
        // gone by the time the request runs. A factory that completes synchronously runs in the caller's own frame.
        await InitializeRequestContext(_plans.Get<TRequest, TResult>(), request, ct).ConfigureAwait(false);
        await EnsureQueueConsumerStartedAsync(ct).ConfigureAwait(false);

        // Capture the caller's trace context so the queued execution's spans link back to the originating request.
        var parentContext = Activity.Current?.Context ?? default;
        return await _backgroundTaskManager.EnqueueAsync<TResult>(
            async workerToken =>
            {
                // The queue withdraws an item whose caller gives up while it waits; this catches a caller that gave up
                // between the dequeue and here, so no consumer slot is spent on it.
                ct.ThrowIfCancellationRequested();

                using var dispatch = StartQueuedDispatchActivity<TRequest>(parentContext);

                if (!ct.CanBeCanceled)
                    return await ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, contextCaptured: true, workerToken)
                        .ConfigureAwait(false);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, workerToken);
                return await ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, contextCaptured: true, linkedCts.Token)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    // RunMode.Queued only completes when the background consumer runs the queued work item, and the consumer only runs
    // once the Generic Host has started its hosted services. Without a running host nothing drains the queue and the
    // dispatch would hang forever — so wait (bounded) for the consumer's readiness signal and throw a clear error if it
    // never comes. In a normal app the consumer started long ago, so the signal is already set and this is a no-op.
    private async Task EnsureQueueConsumerStartedAsync(CancellationToken ct)
    {
        if (_consumerReadiness.Started.IsCompleted) return;

        var timeout = _consumerStartTimeout;
        var delayTask = Task.Delay(timeout, _timeProvider, ct);
        if (await Task.WhenAny(_consumerReadiness.Started, delayTask).ConfigureAwait(false) != delayTask)
            return;

        // ct firing cancels delayTask — observe it so a cancelled dispatch surfaces OperationCanceledException.
        await delayTask.ConfigureAwait(false);

        throw new InvalidOperationException(
            $"RunMode.Queued requires a running host, but the background task-queue consumer did not start within {timeout}. " +
            "Start the Generic Host (host.RunAsync()/StartAsync) before dispatching, switch to RunMode.Inline (the default), " +
            "or increase BackgroundTaskQueueOptions.ConsumerStartTimeout.");
    }

    private async Task<TResult> ExecuteInNewScopeAsync<TRequest, TResult>(
        string activityName,
        TRequest request,
        bool contextCaptured,
        CancellationToken ct)
        where TRequest : IRequest
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            return await ExecuteInProviderAsync<TRequest, TResult>(activityName, request, scope.ServiceProvider, contextCaptured, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The provider is the scope the request runs in. Its context comes from the caller's scope (this executor's) all the
    // same: built first, unless it was captured before the request was handed to the queue.
    private Task<TResult> ExecuteInProviderAsync<TRequest, TResult>(
        string activityName,
        TRequest request,
        IServiceProvider provider,
        bool contextCaptured,
        CancellationToken ct)
        where TRequest : IRequest
    {
        // Untraced, unmetered and with the outbox off (the defaults) there is nothing to settle when the request ends, so
        // nothing needs to wrap the pipeline: hand back its task directly. Only an asynchronously hydrated context needs
        // an await.
        if (!_outboxEnabled && !_metrics.RequestDuration.Enabled && !CqrsActivitySource.Instance.HasListeners())
            try
            {
                var plan = _plans.Get<TRequest, TResult>();
                var contextReady = contextCaptured ? default : InitializeRequestContext(plan, request, ct);
                return contextReady.IsCompletedSuccessfully
                    ? ExecutePipelineAsync(plan, request, provider.GetRequiredService(plan.HandlerType), provider, ct)
                    : AwaitContextThenExecuteAsync(plan, contextReady, request, provider, ct);
            }
            catch (Exception ex)
            {
                return Task.FromException<TResult>(ex);
            }

        // The span is started inside ExecuteSettledAsync, never here: Activity.Current is an AsyncLocal, and setting it
        // in this synchronous frame would write it into the caller's ExecutionContext, leaving the finished span current
        // for everything the caller does next (and parenting its next request to this one).
        return ExecuteSettledAsync<TRequest, TResult>(activityName, request, provider, contextCaptured, ct);
    }

    private async Task<TResult> AwaitContextThenExecuteAsync<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        ValueTask contextReady,
        TRequest request,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IRequest
    {
        await contextReady.ConfigureAwait(false);
        return await ExecutePipelineAsync(plan, request, provider.GetRequiredService(plan.HandlerType), provider, ct).ConfigureAwait(false);
    }

    // The traced and/or outbox-owning path: the activity status and the outbox buffer are settled when the request ends.
    // Being async, it restores the caller's Activity.Current when it returns.
    private async Task<TResult> ExecuteSettledAsync<TRequest, TResult>(
        string activityName,
        TRequest request,
        IServiceProvider provider,
        bool contextCaptured,
        CancellationToken ct)
        where TRequest : IRequest
    {
        using var activity = CqrsActivitySource.StartRequest(activityName, typeof(TRequest));

        // The request runs as an owner of the scope's outbox buffer: what it (and its behaviors) buffers and a unit of
        // work does not take is stored when it succeeds and discarded when it fails. It settles only its own - never a
        // sibling's running beside it in the scope, nor a nested request's that is still running.
        var outboxScope = _outboxEnabled ? RequestOutboxScope.Begin(provider) : null;
        var outboxSettled = false;
        var metered = _metrics.RequestDuration.Enabled;
        var startedAt = metered ? _timeProvider.GetTimestamp() : 0L;
        var kind = ReferenceEquals(activityName, CommandActivityName) ? "command" : "query";
        try
        {
            var plan = _plans.Get<TRequest, TResult>();
            if (!contextCaptured)
                await InitializeRequestContext(plan, request, ct).ConfigureAwait(false);
            var handler = provider.GetRequiredService(plan.HandlerType);
            var result = await ExecutePipelineAsync(plan, request, handler, provider, ct).ConfigureAwait(false);

            // A command that returns a failed result did not do what it was asked to: its notifications announce work
            // that did not happen, and to the span and the metric alike it is a failure.
            var failedResult = result is CommandResult { IsSuccess: false } failed ? failed : null;

            outboxSettled = true;
            if (outboxScope is { } completedScope)
            {
                if (failedResult is not null)
                    completedScope.Abandon();
                else
                    await completedScope.CompleteAsync().ConfigureAwait(false);
            }

            if (failedResult is not null)
                activity?.SetStatus(ActivityStatusCode.Error, failedResult.ErrorMessage);
            else
                activity?.SetStatus(ActivityStatusCode.Ok);

            if (metered)
                _metrics.RecordRequest(typeof(TRequest), kind, failedResult is null, _timeProvider.GetElapsedTime(startedAt));

            return result;
        }
        catch (Exception ex)
        {
            if (!outboxSettled) outboxScope?.Abandon();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (metered)
                _metrics.RecordRequest(typeof(TRequest), kind, false, _timeProvider.GetElapsedTime(startedAt));

            throw;
        }
    }

    private static Activity? StartQueuedDispatchActivity<TRequest>(ActivityContext parentContext)
    {
        if (!CqrsActivitySource.Instance.HasListeners()) return null;

        var activity = CqrsActivitySource.Instance.StartActivity($"CQRS Queued {typeof(TRequest).Name}", ActivityKind.Internal, parentContext);
        activity?.SetTag(CqrsTelemetry.Tags.RequestType, CqrsTelemetry.TypeName(typeof(TRequest)));
        return activity;
    }

    /// <summary>
    ///     Sets <see cref="IRequest.Context" /> from the request's context factory, replacing any context the request
    ///     arrived with: a request bound from an HTTP body or read from a message carries whatever its sender put there,
    ///     and the identity a handler reads must come from the application. Uses the cached plan, so no registry lookup
    ///     happens, and builds the built-in default context directly rather than through a transient factory resolved
    ///     from DI. Completes synchronously unless a custom factory hydrates asynchronously.
    /// </summary>
    /// <remarks>
    ///     The factory is resolved from this executor's scope, the caller's, and never from the scope the request runs
    ///     in: the context describes who sent the request, and a scope of the request's own (or the queue consumer's
    ///     flow) holds a fresh instance of every scoped service the factory reads the caller from.
    /// </remarks>
    private ValueTask InitializeRequestContext<TRequest>(
        RequestPlanBase plan,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        if (plan.UsesDefaultContextFactory)
        {
            // Exactly what the built-in factory does, minus the DI resolution: stamped from the application's clock.
            request.Context = new RequestContextBase(_timeProvider.GetUtcNow().UtcDateTime);
            return default;
        }

        if (plan.ContextSource is not { } source || !source.TryCreate(_services, request, cancellationToken, out var pending))
            throw new InvalidOperationException(
                $"No IRequestContextFactory<{plan.ContextType.Name}> is registered for context type '{plan.ContextType.FullName}'. " +
                "Declare one (the source generator registers it) or register one in DI, or use the default context " +
                "(CommandBase/QueryBase without a custom context type).");

        if (pending.IsCompletedSuccessfully)
        {
            request.Context = Stamped(pending.Result);
            return default;
        }

        return new ValueTask(AssignWhenCreated(pending, request));

        async Task AssignWhenCreated(ValueTask<IRequestContext> creating, TRequest target)
        {
            target.Context = Stamped(await creating.ConfigureAwait(false));
        }
    }

    // A custom context built with the parameterless RequestContextBase() constructor carries no timestamp of its own:
    // it takes the application's clock here, as the built-in factory's contexts do. One that passed a time keeps it.
    private IRequestContext Stamped(IRequestContext context)
    {
        if (context is RequestContextBase { IsTimestampPending: true } pending)
            pending.StampFromApplicationClock(_timeProvider.GetUtcNow().UtcDateTime);
        return context;
    }
}

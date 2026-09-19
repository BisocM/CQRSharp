using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Orchestrates the execution of commands and queries through a series of behaviors (the pipeline).
///     This is the central component that connects a request to its handler, wrapping it with cross-cutting concerns
///     like logging, transactions, validation, etc.
/// </summary>
/// <remarks>
///     Split across partial files: this one holds the entry points, run-mode/scope routing and request-context
///     initialization; <c>PipelineExecutor.Pipeline.cs</c> the behavior chain and the final handler action;
///     <c>PipelineExecutor.Streaming.cs</c> the streaming equivalents; <c>PipelineExecutor.Interceptors.cs</c> the
///     pre/post-handler attributes; and <c>PipelineExecutor.Ordering.cs</c> the priority comparers.
/// </remarks>
public sealed partial class PipelineExecutor : IPipelineExecutor
{
    private const string CommandActivityName = "CQRS Command";
    private const string QueryActivityName = "CQRS Query";
    private const string StreamActivityName = "CQRS Stream";

    private readonly IBackgroundTaskManager _backgroundTaskManager;
    private readonly IOptions<DispatcherOptions> _dispatcherOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _services;
    private readonly IRequestRegistry _requestRegistry;
    private readonly IHandlerRegistry _handlerRegistry;
    private readonly IContextFactoryRegistry _contextFactoryRegistry;

    // The per-provider plan cache (a singleton). An executor built by hand without it — unit tests — gets a private one.
    private readonly RequestPlanCache _plans;

    // Resolved once: with the outbox disabled (the default) the request path never touches the outbox services.
    private readonly bool _outboxEnabled;

    /// <summary>Creates an executor over explicitly supplied registries.</summary>
    public PipelineExecutor(
        IServiceProvider serviceProvider,
        IRequestRegistry requestRegistry,
        IHandlerRegistry handlerRegistry,
        IContextFactoryRegistry contextFactoryRegistry,
        IOptions<DispatcherOptions> dispatcherOptions,
        IBackgroundTaskManager backgroundTaskManager)
    {
        _services = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _backgroundTaskManager = backgroundTaskManager ?? throw new ArgumentNullException(nameof(backgroundTaskManager));
        _dispatcherOptions = dispatcherOptions ?? throw new ArgumentNullException(nameof(dispatcherOptions));
        _requestRegistry = requestRegistry;
        _handlerRegistry = handlerRegistry;
        _contextFactoryRegistry = contextFactoryRegistry;
        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _plans = serviceProvider.GetService<RequestPlanCache>()
                 ?? new RequestPlanCache(serviceProvider, requestRegistry, handlerRegistry, contextFactoryRegistry);
        _outboxEnabled = IsOutboxEnabled(serviceProvider.GetService<IOptions<OutboxOptions>>());
    }

    // The DI path: an executor is created per scope (per web request), so everything that is the same for every scope
    // arrives pre-resolved in one singleton instead of being looked up again each time.
    internal PipelineExecutor(IServiceProvider serviceProvider, PipelineExecutorShared shared)
    {
        _services = serviceProvider;
        _backgroundTaskManager = shared.BackgroundTaskManager;
        _dispatcherOptions = shared.DispatcherOptions;
        _requestRegistry = shared.RequestRegistry;
        _handlerRegistry = shared.HandlerRegistry;
        _contextFactoryRegistry = shared.ContextFactoryRegistry;
        _scopeFactory = shared.ScopeFactory;
        _plans = shared.Plans;
        _outboxEnabled = shared.OutboxEnabled;
        Shared = shared;
    }

    /// <summary>The provider-wide singleton this executor was built from; <c>null</c> for a hand-constructed executor.</summary>
    internal PipelineExecutorShared? Shared { get; }

    internal static bool IsOutboxEnabled(IOptions<OutboxOptions>? options)
        => options?.Value.Mode is OutboxMode.Enabled or OutboxMode.Transactional;

    /// <summary>
    ///     Executes a query. When <see cref="RunMode.Queued" /> is configured the work is handed to the background
    ///     task queue and the returned task completes when the queued work finishes; otherwise it runs inline.
    ///     In both cases the request context is initialized, the behavior pipeline is chained, and the final
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
        return ExecuteAsync<TRequest, TResult>(QueryActivityName, query, ct);
    }

    /// <summary>
    ///     Executes a command. When <see cref="RunMode.Queued" /> is configured the work is handed to the background
    ///     task queue and the returned task completes when the queued work finishes; otherwise it runs inline.
    ///     In both cases the request context is initialized, the behavior pipeline is chained, and the final
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

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> ExecuteStreamAsync<TRequest, TItem>(TRequest request, CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_dispatcherOptions.Value.RunMode == RunMode.Queued)
            throw new InvalidOperationException("RunMode.Queued is not supported for streaming requests.");

        return _dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New
            ? ExecuteStreamInNewScope<TRequest, TItem>(request, ct)
            : ExecuteStreamInProvider<TRequest, TItem>(request, _services, ct);
    }

    // Commands and queries share one execution path; only the activity name differs.
    private Task<TResult> ExecuteAsync<TRequest, TResult>(string activityName, TRequest request, CancellationToken ct)
        where TRequest : IRequest
    {
        if (_dispatcherOptions.Value.RunMode == RunMode.Queued)
            return ExecuteQueuedAsync<TRequest, TResult>(activityName, request, ct);

        return _dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New
            ? ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, ct)
            : ExecuteInProviderAsync<TRequest, TResult>(activityName, request, _services, ct);
    }

    private async Task<TResult> ExecuteQueuedAsync<TRequest, TResult>(string activityName, TRequest request, CancellationToken ct)
        where TRequest : IRequest
    {
        await EnsureQueueConsumerStartedAsync(ct).ConfigureAwait(false);

        // Capture the caller's trace context so the queued execution's spans link back to the originating request.
        var parentContext = Activity.Current?.Context ?? default;
        return await _backgroundTaskManager.EnqueueAsync<TResult>(
            async workerToken =>
            {
                using var dispatch = StartQueuedDispatchActivity<TRequest>(parentContext);

                if (!ct.CanBeCanceled)
                    return await ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, workerToken).ConfigureAwait(false);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, workerToken);
                return await ExecuteInNewScopeAsync<TRequest, TResult>(activityName, request, linkedCts.Token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    // RunMode.Queued only completes when the background consumer runs the queued work item, and the consumer only runs
    // once the Generic Host has started its hosted services. Without a running host nothing drains the queue and the
    // dispatch would hang forever — so wait (bounded) for the consumer's readiness signal and throw a clear error if it
    // never comes. In a normal app the consumer started long ago, so the signal is already set and this is a no-op.
    private async Task EnsureQueueConsumerStartedAsync(CancellationToken ct)
    {
        var readiness = _services.GetService<ConsumerReadiness>();
        if (readiness is null || readiness.Started.IsCompleted) return;

        var timeout = _services.GetService<IOptions<BackgroundTaskQueueOptions>>()?.Value.ConsumerStartTimeout
                      ?? TimeSpan.FromSeconds(10);
        var timeProvider = _services.GetService<TimeProvider>() ?? TimeProvider.System;

        var delayTask = Task.Delay(timeout, timeProvider, ct);
        if (await Task.WhenAny(readiness.Started, delayTask).ConfigureAwait(false) != delayTask)
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
        CancellationToken ct)
        where TRequest : IRequest
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await ExecuteInProviderAsync<TRequest, TResult>(activityName, request, scope.ServiceProvider, ct).ConfigureAwait(false);
    }

    private Task<TResult> ExecuteInProviderAsync<TRequest, TResult>(
        string activityName,
        TRequest request,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IRequest
    {
        var activity = CqrsActivitySource.StartRequest(activityName, typeof(TRequest));

        // Untraced with the outbox off (the defaults) there is nothing to settle when the request ends, so nothing needs
        // to wrap the pipeline: hand back its task directly. Only an asynchronously hydrated context needs an await.
        if (activity is null && !_outboxEnabled)
            try
            {
                var plan = _plans.Get<TRequest, TResult>();
                var contextReady = InitializeRequestContext(plan, request, provider, ct);
                return contextReady.IsCompletedSuccessfully
                    ? ExecutePipelineAsync(plan, request, provider.GetRequiredService(plan.HandlerType), provider, ct)
                    : AwaitContextThenExecuteAsync(plan, contextReady, request, provider, ct);
            }
            catch (Exception ex)
            {
                return Task.FromException<TResult>(ex);
            }

        return ExecuteSettledAsync<TRequest, TResult>(activity, request, provider, ct);
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
    private async Task<TResult> ExecuteSettledAsync<TRequest, TResult>(
        Activity? startedActivity,
        TRequest request,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IRequest
    {
        using var activity = startedActivity;

        // The request owns the scoped outbox for its duration: whatever a unit-of-work behavior does not drain is
        // persisted on success and discarded on failure, so a buffered notification is never silently dropped.
        var outboxScope = _outboxEnabled ? RequestOutboxScope.Begin(provider) : null;
        var outboxSettled = false;
        try
        {
            var plan = _plans.Get<TRequest, TResult>();
            await InitializeRequestContext(plan, request, provider, ct).ConfigureAwait(false);
            var handler = provider.GetRequiredService(plan.HandlerType);
            var result = await ExecutePipelineAsync(plan, request, handler, provider, ct).ConfigureAwait(false);

            outboxSettled = true;
            if (outboxScope is { } completedScope)
                await completedScope.CompleteAsync(ct).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            if (!outboxSettled) outboxScope?.Abandon();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static Activity? StartQueuedDispatchActivity<TRequest>(ActivityContext parentContext)
        => CqrsActivitySource.Instance.HasListeners()
            ? CqrsActivitySource.Instance.StartActivity($"CQRS Queued {typeof(TRequest).Name}", ActivityKind.Internal, parentContext)
            : null;

    /// <summary>
    ///     Resolves the specific handler for a request type from the scoped DI container.
    /// </summary>
    /// <param name="requestType">The type of the request.</param>
    /// <param name="scopedProvider">The scoped service provider for this request.</param>
    /// <returns>The resolved handler instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handler type is not registered or cannot be resolved.</exception>
    private object GetHandler(Type requestType, IServiceProvider scopedProvider)
    {
        // Find the handler type from the source-generated registry.
        var handlerType = _requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        // Resolve the handler instance from the current scope.
        return scopedProvider.GetRequiredService(handlerType)
               ?? throw new InvalidOperationException($"Handler instance '{handlerType.Name}' not available.");
    }

    // Command/query variant of the context initialization below, driven by the cached plan: no registry lookups, and the
    // built-in default context is constructed directly rather than through a transient factory resolved from DI.
    private static ValueTask InitializeRequestContext<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        TRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        request.Metadata = plan.Metadata;

        // If the context is already set (e.g., manually by the caller), do nothing.
        if (request.Context is not null) return default;

        if (plan.UsesDefaultContextFactory)
        {
            request.Context = new RequestContextBase();
            return default;
        }

        if (plan.ContextFactoryResolver?.Invoke(services) is not IInternalRequestContextFactory contextFactory)
            throw new InvalidOperationException(
                $"No IRequestContextFactory<{plan.ContextType.Name}> is registered for context type '{plan.ContextType.FullName}'. " +
                "Register one in DI, or use the default context (CommandBase/QueryBase without a custom context type).");

        var pending = contextFactory.CreateContextAsync(request, cancellationToken);
        if (pending.IsCompletedSuccessfully)
        {
            request.Context = pending.Result;
            return default;
        }

        return new ValueTask(AssignWhenCreated(pending, request));

        static async Task AssignWhenCreated(ValueTask<IRequestContext> creating, TRequest target)
        {
            target.Context = await creating.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Initializes the <see cref="IRequest.Metadata" /> and <see cref="IRequest.Context" /> on the request object.
    ///     This ensures the request is enriched with necessary information before it enters the pipeline.
    /// </summary>
    /// <param name="requestBase">The request object.</param>
    /// <param name="services">The scoped service provider used to resolve the context factory.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous context creation.</param>
    /// <exception cref="InvalidOperationException">Thrown if metadata or a required context factory is not found.</exception>
    private async ValueTask InitializeRequestContextAsync(IRequest requestBase, IServiceProvider services, CancellationToken cancellationToken)
    {
        // Retrieve and assign source-generated metadata to the request object.
        if (!_requestRegistry.TryGetRequestMetadata(requestBase.GetType(), out var metadata) || metadata == null)
            throw new InvalidOperationException($"No metadata for request '{requestBase.GetType().Name}'.");

        requestBase.Metadata = metadata;

        // If the context is already set (e.g., manually by the caller), do nothing.
        if (requestBase.Context != null) return;

        // Determine the required context type from metadata or default to RequestContextBase.
        var contextType = metadata.ContextType ?? typeof(RequestContextBase);

        // Find the appropriate factory for creating an instance of the context.
        var factoryObj = _contextFactoryRegistry.TryGetFactory(contextType, services);

        if (factoryObj is not IInternalRequestContextFactory contextFactory)
            throw new InvalidOperationException(
                $"No IRequestContextFactory<{contextType.Name}> is registered for context type '{contextType.FullName}'. " +
                "Register one in DI, or use the default context (CommandBase/QueryBase without a custom context type).");

        // Create and assign the context to the request. The factory may load request-scoped data asynchronously
        // (the default implementation wraps a synchronous CreateContext, so existing factories complete inline).
        requestBase.Context = await contextFactory.CreateContextAsync(requestBase, cancellationToken).ConfigureAwait(false);
    }
}

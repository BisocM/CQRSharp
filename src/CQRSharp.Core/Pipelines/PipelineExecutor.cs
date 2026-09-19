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
/// <param name="serviceProvider">The current DI scope service provider.</param>
/// <param name="requestRegistry">The registry for request metadata.</param>
/// <param name="handlerRegistry">The registry for compiled handler invokers.</param>
/// <param name="contextFactoryRegistry">The registry for request context factories.</param>
/// <param name="dispatcherOptions">Configuration options controlling sync vs queued execution.</param>
/// <param name="backgroundTaskManager">Background task queue used for queued execution.</param>
public sealed partial class PipelineExecutor(
    IServiceProvider serviceProvider,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IContextFactoryRegistry contextFactoryRegistry,
    IOptions<DispatcherOptions> dispatcherOptions,
    IBackgroundTaskManager backgroundTaskManager) : IPipelineExecutor
{
    private const string CommandActivityName = "CQRS Command";
    private const string QueryActivityName = "CQRS Query";
    private const string StreamActivityName = "CQRS Stream";

    private readonly IBackgroundTaskManager _backgroundTaskManager = backgroundTaskManager ?? throw new ArgumentNullException(nameof(backgroundTaskManager));
    private readonly IOptions<DispatcherOptions> _dispatcherOptions = dispatcherOptions ?? throw new ArgumentNullException(nameof(dispatcherOptions));
    private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    private readonly IServiceProvider _services = serviceProvider;

    // Resolved once: with the outbox disabled (the default) the request path never touches the outbox services.
    private readonly bool _outboxEnabled =
        serviceProvider.GetService<IOptions<OutboxOptions>>()?.Value.Mode is OutboxMode.Enabled or OutboxMode.Transactional;

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

    private async Task<TResult> ExecuteInProviderAsync<TRequest, TResult>(
        string activityName,
        TRequest request,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IRequest
    {
        using var activity = CqrsActivitySource.StartRequest(activityName, typeof(TRequest));

        // The request owns the scoped outbox for its duration: whatever a unit-of-work behavior does not drain is
        // persisted on success and discarded on failure, so a buffered notification is never silently dropped.
        var outboxScope = _outboxEnabled ? RequestOutboxScope.Begin(provider) : null;
        var outboxSettled = false;
        try
        {
            await InitializeRequestContextAsync(request, provider, ct).ConfigureAwait(false);
            var handler = GetHandler(typeof(TRequest), provider);
            var result = await ExecutePipelineAsync<TRequest, TResult>(request, handler, provider, ct).ConfigureAwait(false);

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
        var handlerType = requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        // Resolve the handler instance from the current scope.
        return scopedProvider.GetRequiredService(handlerType)
               ?? throw new InvalidOperationException($"Handler instance '{handlerType.Name}' not available.");
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
        if (!requestRegistry.TryGetRequestMetadata(requestBase.GetType(), out var metadata) || metadata == null)
            throw new InvalidOperationException($"No metadata for request '{requestBase.GetType().Name}'.");

        requestBase.Metadata = metadata;

        // If the context is already set (e.g., manually by the caller), do nothing.
        if (requestBase.Context != null) return;

        // Determine the required context type from metadata or default to RequestContextBase.
        var contextType = metadata.ContextType ?? typeof(RequestContextBase);

        // Find the appropriate factory for creating an instance of the context.
        var factoryObj = contextFactoryRegistry.TryGetFactory(contextType, services);

        if (factoryObj is not IInternalRequestContextFactory contextFactory)
            throw new InvalidOperationException(
                $"No IRequestContextFactory<{contextType.Name}> is registered for context type '{contextType.FullName}'. " +
                "Register one in DI, or use the default context (CommandBase/QueryBase without a custom context type).");

        // Create and assign the context to the request. The factory may load request-scoped data asynchronously
        // (the default implementation wraps a synchronous CreateContext, so existing factories complete inline).
        requestBase.Context = await contextFactory.CreateContextAsync(requestBase, cancellationToken).ConfigureAwait(false);
    }
}

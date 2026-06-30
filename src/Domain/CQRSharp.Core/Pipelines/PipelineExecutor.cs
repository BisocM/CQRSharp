using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
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
/// <param name="serviceProvider">The current DI scope service provider.</param>
/// <param name="requestRegistry">The registry for request metadata.</param>
/// <param name="handlerRegistry">The registry for compiled handler invokers.</param>
/// <param name="contextFactoryRegistry">The registry for request context factories.</param>
/// <param name="dispatcherOptions">Configuration options controlling sync vs queued execution.</param>
/// <param name="backgroundTaskManager">Background task queue used for queued execution.</param>
public sealed class PipelineExecutor(
    IServiceProvider serviceProvider,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IContextFactoryRegistry contextFactoryRegistry,
    IOptions<DispatcherOptions> dispatcherOptions,
    IBackgroundTaskManager backgroundTaskManager) : IPipelineExecutor
{
    private const int DefaultBehaviorPriority = IPrioritizedPipelineBehavior.DefaultPriority;
    private static readonly ConcurrentDictionary<Type, IPreHandlerAttribute[]> SortedPreHandlerCache = new();
    private static readonly ConcurrentDictionary<Type, IPostHandlerAttribute[]> SortedPostHandlerCache = new();
    private readonly IBackgroundTaskManager _backgroundTaskManager = backgroundTaskManager ?? throw new ArgumentNullException(nameof(backgroundTaskManager));
    private readonly IOptions<DispatcherOptions> _dispatcherOptions = dispatcherOptions ?? throw new ArgumentNullException(nameof(dispatcherOptions));
    private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    private readonly IServiceProvider _services = serviceProvider;

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
        if (_dispatcherOptions.Value.RunMode == RunMode.Queued)
        {
            // Capture the caller's trace context so the queued execution's spans link back to the originating request.
            var parentContext = Activity.Current?.Context ?? default;
            return _backgroundTaskManager.EnqueueAsync<TResult>(
                async workerToken =>
                {
                    using var dispatch = StartQueuedDispatchActivity<TRequest>(parentContext);

                    if (!ct.CanBeCanceled)
                        return await ExecuteQueryInNewScopeAsync<TRequest, TResult>(query, workerToken).ConfigureAwait(false);

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, workerToken);
                    return await ExecuteQueryInNewScopeAsync<TRequest, TResult>(query, linkedCts.Token).ConfigureAwait(false);
                },
                ct);
        }

        return ExecuteQueryImmediateAsync<TRequest, TResult>(query, ct);
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
        if (_dispatcherOptions.Value.RunMode == RunMode.Queued)
        {
            // Capture the caller's trace context so the queued execution's spans link back to the originating request.
            var parentContext = Activity.Current?.Context ?? default;
            return _backgroundTaskManager.EnqueueAsync<CommandResult>(
                async workerToken =>
                {
                    using var dispatch = StartQueuedDispatchActivity<TRequest>(parentContext);

                    if (!ct.CanBeCanceled)
                        return await ExecuteCommandInNewScopeAsync(command, workerToken).ConfigureAwait(false);

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, workerToken);
                    return await ExecuteCommandInNewScopeAsync(command, linkedCts.Token).ConfigureAwait(false);
                },
                ct);
        }

        return ExecuteCommandImmediateAsync(command, ct);
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

    private Task<TResult> ExecuteQueryImmediateAsync<TRequest, TResult>(
        TRequest query, CancellationToken ct)
        where TRequest : IRequest<TResult>
    {
        return _dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New
            ? ExecuteQueryInNewScopeAsync<TRequest, TResult>(query, ct)
            : ExecuteQueryInProviderAsync<TRequest, TResult>(query, _services, ct);
    }

    private async Task<TResult> ExecuteQueryInNewScopeAsync<TRequest, TResult>(
        TRequest query,
        CancellationToken ct)
        where TRequest : IRequest<TResult>
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await ExecuteQueryInProviderAsync<TRequest, TResult>(query, scope.ServiceProvider, ct).ConfigureAwait(false);
    }

    private async Task<TResult> ExecuteQueryInProviderAsync<TRequest, TResult>(
        TRequest query,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IRequest<TResult>
    {
        using var activity = CqrsActivitySource.StartRequest("CQRS Query", typeof(TRequest));
        try
        {
            InitializeRequestContext(query, provider);
            var handler = GetHandler(typeof(TRequest), provider);
            var result = await ExecutePipelineAsync<TRequest, TResult>(query, handler, provider, ct).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private IAsyncEnumerable<TItem> ExecuteStreamInNewScope<TRequest, TItem>(
        TRequest request,
        CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync([EnumeratorCancellation] CancellationToken enumeratorToken = default)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            using var linked = CreateLinkedCancellationTokenSource(ct, enumeratorToken);
            var token = linked?.Token ?? (ct.CanBeCanceled ? ct : enumeratorToken);

            await foreach (var item in ExecuteStreamInProviderCore<TRequest, TItem>(request, scope.ServiceProvider, token)
                               .WithCancellation(token)
                               .ConfigureAwait(false))
                yield return item;
        }
    }

    private IAsyncEnumerable<TItem> ExecuteStreamInProvider<TRequest, TItem>(
        TRequest request,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync([EnumeratorCancellation] CancellationToken enumeratorToken = default)
        {
            using var linked = CreateLinkedCancellationTokenSource(ct, enumeratorToken);
            var token = linked?.Token ?? (ct.CanBeCanceled ? ct : enumeratorToken);

            await foreach (var item in ExecuteStreamInProviderCore<TRequest, TItem>(request, provider, token)
                               .WithCancellation(token)
                               .ConfigureAwait(false))
                yield return item;
        }
    }

    private IAsyncEnumerable<TItem> ExecuteStreamInProviderCore<TRequest, TItem>(
        TRequest request,
        IServiceProvider provider,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        InitializeRequestContext(request, provider);

        var handler = GetHandler(typeof(TRequest), provider);

        var pipeline = ExecuteStreamPipeline<TRequest, TItem>(request, handler, provider, cancellationToken);
        return WithActivity(pipeline);

        async IAsyncEnumerable<TItem> WithActivity(IAsyncEnumerable<TItem> source)
        {
            using var activity = CqrsActivitySource.StartRequest("CQRS Stream", typeof(TRequest));
            await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    private Task<CommandResult> ExecuteCommandImmediateAsync<TRequest>(
        TRequest command, CancellationToken ct)
        where TRequest : ICommand
    {
        return _dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New
            ? ExecuteCommandInNewScopeAsync(command, ct)
            : ExecuteCommandInProviderAsync(command, _services, ct);
    }

    private async Task<CommandResult> ExecuteCommandInNewScopeAsync<TRequest>(
        TRequest command,
        CancellationToken ct)
        where TRequest : ICommand
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await ExecuteCommandInProviderAsync(command, scope.ServiceProvider, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> ExecuteCommandInProviderAsync<TRequest>(
        TRequest command,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : ICommand
    {
        using var activity = CqrsActivitySource.StartRequest("CQRS Command", typeof(TRequest));
        try
        {
            InitializeRequestContext(command, provider);
            var handler = GetHandler(typeof(TRequest), provider);
            var result = await ExecutePipelineAsync<TRequest, CommandResult>(command, handler, provider, ct).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static Activity? StartQueuedDispatchActivity<TRequest>(ActivityContext parentContext)
        => CqrsActivitySource.Instance.StartActivity($"CQRS Queued {typeof(TRequest).Name}", ActivityKind.Internal, parentContext);

    private IAsyncEnumerable<TItem> ExecuteStreamPipeline<TRequest, TItem>(
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        // Fresh per-resolution array (Microsoft DI); the in-place filter/sort below is concurrency-safe — see the note
        // in ExecutePipelineAsync.
        var resolved = services.GetServices<IStreamPipelineBehavior<TRequest, TItem>>();
        var behaviors = resolved as IStreamPipelineBehavior<TRequest, TItem>[] ?? resolved.ToArray();

        var exemptions = request.Metadata?.PipelineExemptions;
        var behaviorCount = behaviors.Length;
        if (exemptions is { Length: > 0 } && behaviorCount > 0)
        {
            var write = 0;
            for (var read = 0; read < behaviorCount; read++)
            {
                var behavior = behaviors[read];
                if (IsExempted(behavior.GetType(), exemptions)) continue;
                behaviors[write++] = behavior;
            }

            behaviorCount = write;
        }

        if (behaviorCount == 0)
            return ExecuteFinalStreamAction<TRequest, TItem>(request, handler, services, cancellationToken);

        if (behaviorCount > 1)
            Array.Sort(behaviors, 0, behaviorCount, StreamBehaviorPriorityComparer<TRequest, TItem>.Instance);

        return InvokeBehavior(0, cancellationToken);

        IAsyncEnumerable<TItem> InvokeBehavior(int index, CancellationToken ct)
        {
            if (index >= behaviorCount)
                return ExecuteFinalStreamAction<TRequest, TItem>(request, handler, services, ct);

            var behavior = behaviors[index];
            return behavior.Handle(request, nextToken => InvokeBehavior(index + 1, nextToken), ct);
        }
    }

    private static bool IsExempted(Type behaviorType, ReadOnlySpan<PipelineExemptionAttribute> exemptions)
    {
        if (exemptions.Length == 0) return false;

        var isBehaviorGeneric = behaviorType.IsGenericType;
        var behaviorTypeDefinition = isBehaviorGeneric ? behaviorType.GetGenericTypeDefinition() : null;

        foreach (var exemption in exemptions)
        {
            var exemptedType = exemption.ExemptedPipeline;

            // Exact type match (e.g., typeof(MyBehavior<Foo, Bar>)).
            if (exemptedType == behaviorType) return true;

            // Open generic match (e.g., typeof(MyBehavior<,>)).
            if (exemptedType.IsGenericTypeDefinition && isBehaviorGeneric && behaviorTypeDefinition == exemptedType)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Executes the request processing pipeline for a specific request type.
    ///     <para>
    ///         This method resolves all registered <see cref="IPipelineBehavior{TRequest, TResult}" /> instances
    ///         directly from the current request's scoped service provider. It then applies request-level pipeline
    ///         exemptions and executes the behaviors in a deterministic order when they implement
    ///         <see cref="IPrioritizedPipelineBehavior" />.
    ///     </para>
    /// </summary>
    /// <typeparam name="TRequest">The type of the request entering the pipeline.</typeparam>
    /// <typeparam name="TResult">The result type of the request.</typeparam>
    /// <param name="request">The request instance entering the pipeline.</param>
    /// <param name="handler">The resolved handler instance for the request.</param>
    /// <param name="services">The scoped service provider for resolving dependencies.</param>
    /// <param name="cancellationToken">A cancellation token for the request.</param>
    private Task<TResult> ExecutePipelineAsync<TRequest, TResult>(
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        // GetServices returns a freshly-allocated array per resolution (Microsoft DI), so the in-place exemption
        // filter and priority sort below are concurrency-safe: each dispatch owns its array and never mutates a shared
        // or cached collection. (Verified by the hot-path contention stress tests.) Do not change this to a cached array.
        var resolved = services.GetServices<IPipelineBehavior<TRequest, TResult>>();
        var behaviors = resolved as IPipelineBehavior<TRequest, TResult>[] ?? resolved.ToArray();

        // Filter out behaviors that are exempted by request metadata (supports closed and open generic exemptions).
        var exemptions = request.Metadata?.PipelineExemptions;
        var behaviorCount = behaviors.Length;
        if (exemptions is { Length: > 0 } && behaviorCount > 0)
        {
            var write = 0;
            for (var read = 0; read < behaviorCount; read++)
            {
                var behavior = behaviors[read];
                if (IsExempted(behavior.GetType(), exemptions)) continue;
                behaviors[write++] = behavior;
            }

            behaviorCount = write;
        }

        if (behaviorCount == 0)
            return ExecuteFinalActionAsync<TRequest, TResult>(request, handler, services, cancellationToken);

        if (behaviorCount > 1)
            Array.Sort(behaviors, 0, behaviorCount, BehaviorPriorityComparer<TRequest, TResult>.Instance);

        return InvokeBehavior(0, cancellationToken);

        Task<TResult> InvokeBehavior(int index, CancellationToken ct)
        {
            if (index >= behaviorCount)
                return ExecuteFinalActionAsync<TRequest, TResult>(request, handler, services, ct);

            var behavior = behaviors[index];
            return behavior.Handle(
                request,
                nextToken => InvokeBehavior(index + 1, nextToken),
                ct);
        }
    }

    private async Task<TResult> ExecuteFinalActionAsync<TRequest, TResult>(
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        var notificationDispatcher = services.GetRequiredService<INotificationDispatcher>();

        switch (request)
        {
            // Publish notifications to signal the start of command/query handling.
            case ICommand cmd:
                await notificationDispatcher.Publish(new CommandInitiatedNotification(cmd), cancellationToken).ConfigureAwait(false);
                break;
            case IQuery<TResult> qry:
                await notificationDispatcher.Publish(new QueryInitiatedNotification<TResult>(qry), cancellationToken).ConfigureAwait(false);
                break;
        }

        // Execute any pre-handler logic defined via attributes on the request class.
        await InvokePreHandleAttributes(request, services, cancellationToken).ConfigureAwait(false);

        // Invoke the actual handler to process the request. On failure publish the matching *Failed notification so a
        // started request always has a terminal lifecycle notification, then propagate the original exception.
        TResult result;
        try
        {
            result = await HandleRequest<TResult>(request, handler, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            switch (request)
            {
                case ICommand cmdFailed:
                    await notificationDispatcher.Publish(new CommandFailedNotification(cmdFailed, ex), cancellationToken).ConfigureAwait(false);
                    break;
                case IQuery<TResult> qryFailed:
                    await notificationDispatcher.Publish(new QueryFailedNotification<TResult>(qryFailed, ex), cancellationToken).ConfigureAwait(false);
                    break;
            }

            throw;
        }

        // Execute any post-handler logic defined via attributes.
        await InvokePostHandleAttributes(request, services, cancellationToken).ConfigureAwait(false);

        switch (request)
        {
            // Publish notifications to signal the completion of command/query handling.
            case ICommand cmdResult:
                await notificationDispatcher.Publish(new CommandCompletedNotification(cmdResult, (result as CommandResult)!), cancellationToken).ConfigureAwait(false);
                break;
            case IQuery<TResult> qryResult:
                await notificationDispatcher.Publish(new QueryCompletedNotification<TResult>(qryResult, result), cancellationToken).ConfigureAwait(false);
                break;
        }

        return result;
    }

    private IAsyncEnumerable<TItem> ExecuteFinalStreamAction<TRequest, TItem>(
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            var notificationDispatcher = services.GetRequiredService<INotificationDispatcher>();

            await notificationDispatcher.Publish(new StreamInitiatedNotification<TItem>(request), cancellationToken).ConfigureAwait(false);

            await InvokePreHandleAttributes(request, services, cancellationToken).ConfigureAwait(false);

            var stream = await HandleStreamRequest<TItem>(request, handler, cancellationToken).ConfigureAwait(false);

            var yielded = 0L;
            var completed = false;
            Exception? failure = null;

            // Enumerate manually: yield return is not allowed inside a try/catch, so this is the only way to capture a
            // fault from the handler's stream and surface a terminal StreamFailedNotification before rethrowing it.
            await using (var enumerator = stream.GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            completed = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    yielded++;
                    yield return enumerator.Current;
                }
            }

            if (completed)
            {
                await InvokePostHandleAttributes(request, services, cancellationToken).ConfigureAwait(false);
                await notificationDispatcher.Publish(new StreamCompletedNotification<TItem>(request, yielded), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (failure is not null)
            {
                // Plain cancellation is not a fault and the token would also block the publish; just propagate it.
                if (failure is not OperationCanceledException)
                    await notificationDispatcher.Publish(new StreamFailedNotification<TItem>(request, yielded, failure), cancellationToken)
                        .ConfigureAwait(false);

                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            // If neither branch ran the consumer stopped enumerating early without an error; per the documented
            // contract no terminal Completed/Failed notification is published in that case.
        }
    }

    /// <summary>
    ///     Invokes the handler for a given request using a pre-compiled delegate from the handler registry.
    /// </summary>
    /// <typeparam name="TResult">The expected result type.</typeparam>
    /// <param name="request">The request object.</param>
    /// <param name="handler">The resolved handler instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the result from the handler.</returns>
    /// <exception cref="InvalidOperationException">Thrown if a handler delegate is not found for the request type.</exception>
    private async Task<TResult> HandleRequest<TResult>(object request, object handler, CancellationToken cancellationToken)
    {
        if (!handlerRegistry.TryGetHandlerDelegate(request.GetType(), out var handlerDelegate) || handlerDelegate is null)
            throw new InvalidOperationException($"No handler delegate found for request '{request.GetType().Name}'.");

        var result = await handlerDelegate(handler, request, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            if (request is ICommand || default(TResult) is not null)
                throw new InvalidOperationException($"Handler returned null for request '{request.GetType().Name}'.");

            return default!;
        }

        return (TResult)result;
    }

    private async Task<IAsyncEnumerable<TItem>> HandleStreamRequest<TItem>(
        object request,
        object handler,
        CancellationToken cancellationToken)
    {
        if (!handlerRegistry.TryGetHandlerDelegate(request.GetType(), out var handlerDelegate) || handlerDelegate is null)
            throw new InvalidOperationException($"No handler delegate found for request '{request.GetType().Name}'.");

        var result = await handlerDelegate(handler, request, cancellationToken).ConfigureAwait(false);
        if (result is null)
            throw new InvalidOperationException($"Handler returned null stream for request '{request.GetType().Name}'.");

        return (IAsyncEnumerable<TItem>)result;
    }

    private static CancellationTokenSource? CreateLinkedCancellationTokenSource(CancellationToken requestToken, CancellationToken enumeratorToken)
    {
        if (!requestToken.CanBeCanceled || !enumeratorToken.CanBeCanceled) return null;
        return CancellationTokenSource.CreateLinkedTokenSource(requestToken, enumeratorToken);
    }

    /// <summary>
    ///     Discovers and executes all <see cref="IPreHandlerAttribute" />s attached to the request class.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="sp">The scoped service provider.</param>
    /// <param name="ct">The cancellation token.</param>
    private static async Task InvokePreHandleAttributes(IRequest request, IServiceProvider sp, CancellationToken ct)
    {
        if (request.Metadata is null) return;

        var preHandlers = GetPreHandlers(request);
        for (var i = 0; i < preHandlers.Length; i++)
            await preHandlers[i].OnBeforeHandle(request, sp, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Discovers and executes all <see cref="IPostHandlerAttribute" />s attached to the request class.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="sp">The scoped service provider.</param>
    /// <param name="ct">The cancellation token.</param>
    private static async Task InvokePostHandleAttributes(IRequest request, IServiceProvider sp, CancellationToken ct)
    {
        if (request.Metadata is null) return;

        var postHandlers = GetPostHandlers(request);
        for (var i = 0; i < postHandlers.Length; i++)
            await postHandlers[i].OnAfterHandle(request, sp, ct).ConfigureAwait(false);
    }

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
    /// <exception cref="InvalidOperationException">Thrown if metadata or a required context factory is not found.</exception>
    private void InitializeRequestContext(IRequest requestBase, IServiceProvider services)
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
            throw new InvalidOperationException($"No factory for context type '{contextType.FullName}' registered.");

        // Create and assign the context to the request.
        requestBase.Context = contextFactory.CreateContext(requestBase);
    }

    private static IPreHandlerAttribute[] GetPreHandlers(IRequest request)
    {
        var metadata = request.Metadata;
        if (metadata is null || metadata.PreHandlers.Length == 0)
            return [];

        var handlers = metadata.PreHandlers;
        if (handlers.Length <= 1)
            return handlers;

        var sorted = SortedPreHandlerCache.GetOrAdd(
            request.GetType(),
            static (_, source) =>
            {
                var clone = (IPreHandlerAttribute[])source.Clone();
                Array.Sort(clone, PreHandlerPriorityComparer.Instance);
                return clone;
            },
            handlers);

        return sorted;
    }

    private static IPostHandlerAttribute[] GetPostHandlers(IRequest request)
    {
        var metadata = request.Metadata;
        if (metadata is null || metadata.PostHandlers.Length == 0)
            return [];

        var handlers = metadata.PostHandlers;
        if (handlers.Length <= 1)
            return handlers;

        var sorted = SortedPostHandlerCache.GetOrAdd(
            request.GetType(),
            static (_, source) =>
            {
                var clone = (IPostHandlerAttribute[])source.Clone();
                Array.Sort(clone, PostHandlerPriorityComparer.Instance);
                return clone;
            },
            handlers);

        return sorted;
    }

    /// <summary>
    ///     Shared total-ordering tie-break used by the pipeline/handler comparers: order by priority, then by type
    ///     full name so the order is deterministic for equal priorities.
    /// </summary>
    private static int CompareByPriorityThenName(int leftPriority, int rightPriority, object left, object right)
    {
        var byPriority = leftPriority.CompareTo(rightPriority);
        return byPriority != 0
            ? byPriority
            : string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
    }

    private sealed class PreHandlerPriorityComparer : IComparer<IPreHandlerAttribute>
    {
        private PreHandlerPriorityComparer()
        {
        }

        public static PreHandlerPriorityComparer Instance { get; } = new();

        public int Compare(IPreHandlerAttribute? x, IPreHandlerAttribute? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            return CompareByPriorityThenName(x.PreHandlerExecutionPriority, y.PreHandlerExecutionPriority, x, y);
        }
    }

    private sealed class PostHandlerPriorityComparer : IComparer<IPostHandlerAttribute>
    {
        private PostHandlerPriorityComparer()
        {
        }

        public static PostHandlerPriorityComparer Instance { get; } = new();

        public int Compare(IPostHandlerAttribute? x, IPostHandlerAttribute? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            return CompareByPriorityThenName(x.PostHandlerExecutionPriority, y.PostHandlerExecutionPriority, x, y);
        }
    }

    private sealed class BehaviorPriorityComparer<TRequest, TResult> : IComparer<IPipelineBehavior<TRequest, TResult>> where TRequest : IRequest
    {
        private BehaviorPriorityComparer()
        {
        }

        public static BehaviorPriorityComparer<TRequest, TResult> Instance { get; } = new();

        public int Compare(IPipelineBehavior<TRequest, TResult>? x, IPipelineBehavior<TRequest, TResult>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x is IPrioritizedPipelineBehavior lp ? lp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var right = y is IPrioritizedPipelineBehavior rp ? rp.PipelineExecutionPriority : DefaultBehaviorPriority;
            return CompareByPriorityThenName(left, right, x, y);
        }
    }

    private sealed class StreamBehaviorPriorityComparer<TRequest, TItem> : IComparer<IStreamPipelineBehavior<TRequest, TItem>>
        where TRequest : IRequest
    {
        private StreamBehaviorPriorityComparer()
        {
        }

        public static StreamBehaviorPriorityComparer<TRequest, TItem> Instance { get; } = new();

        public int Compare(IStreamPipelineBehavior<TRequest, TItem>? x, IStreamPipelineBehavior<TRequest, TItem>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x is IPrioritizedPipelineBehavior lp ? lp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var right = y is IPrioritizedPipelineBehavior rp ? rp.PipelineExecutionPriority : DefaultBehaviorPriority;
            return CompareByPriorityThenName(left, right, x, y);
        }
    }
}
using System.Collections.Concurrent;
using System.Collections.Generic;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
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
    private static readonly ConcurrentDictionary<Type, IPreHandlerAttribute[]> SortedPreHandlerCache = new();
    private static readonly ConcurrentDictionary<Type, IPostHandlerAttribute[]> SortedPostHandlerCache = new();

    private const int DefaultBehaviorPriority = int.MaxValue / 2;

    private readonly IServiceProvider _services = serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    private readonly IOptions<DispatcherOptions> _dispatcherOptions = dispatcherOptions ?? throw new ArgumentNullException(nameof(dispatcherOptions));
    private readonly IBackgroundTaskManager _backgroundTaskManager = backgroundTaskManager ?? throw new ArgumentNullException(nameof(backgroundTaskManager));

    /// <summary>
    ///     Executes the complete pipeline for a given query.
    ///     It initializes the request context, builds the pipeline by chaining behaviors,
    ///     and invokes the final query handler.
    /// </summary>
    /// <typeparam name="TRequest">The type of the query, which must implement <see cref="IQuery{TResult}" />.</typeparam>
    /// <typeparam name="TResult">The expected result type of the query.</typeparam>
    /// <param name="query">The query object to be executed.</param>
    /// <param name="ct">A cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing the result of the query.</returns>
    public Task<TResult> ExecuteQueryAsync<TRequest, TResult>(
        TRequest query, CancellationToken ct)
        where TRequest : IQuery<TResult>
    {
        if (_dispatcherOptions.Value.RunMode == RunMode.Async)
        {
            return _backgroundTaskManager.EnqueueAsync<TResult>(
                async workerToken =>
                {
                    if (!ct.CanBeCanceled)
                        return await ExecuteQueryInNewScopeAsync<TRequest, TResult>(query, workerToken).ConfigureAwait(false);

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, workerToken);
                    return await ExecuteQueryInNewScopeAsync<TRequest, TResult>(query, linkedCts.Token).ConfigureAwait(false);
                },
                ct);
        }

        return ExecuteQueryImmediateAsync<TRequest, TResult>(query, ct);
    }

    private Task<TResult> ExecuteQueryImmediateAsync<TRequest, TResult>(
        TRequest query, CancellationToken ct)
        where TRequest : IQuery<TResult>
    {
        return _dispatcherOptions.Value.ScopeMode == ExecutionScopeMode.New
            ? ExecuteQueryInNewScopeAsync<TRequest, TResult>(query, ct)
            : ExecuteQueryInProviderAsync<TRequest, TResult>(query, _services, ct);
    }

    private async Task<TResult> ExecuteQueryInNewScopeAsync<TRequest, TResult>(
        TRequest query,
        CancellationToken ct)
        where TRequest : IQuery<TResult>
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await ExecuteQueryInProviderAsync<TRequest, TResult>(query, scope.ServiceProvider, ct).ConfigureAwait(false);
    }

    private async Task<TResult> ExecuteQueryInProviderAsync<TRequest, TResult>(
        TRequest query,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IQuery<TResult>
    {
        // Ensure the request has its metadata and a valid context before processing.
        InitializeRequestContext(query, provider);

        // Resolve the specific handler for this request from the selected provider.
        var handler = GetHandler(typeof(TRequest), provider);

        // Execute the request through the resolved pipeline behaviors and handler.
        return await ExecutePipelineAsync<TRequest, TResult>(query, handler, provider, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Executes the complete pipeline for a given command.
    ///     It initializes the request context, builds the pipeline by chaining behaviors,
    ///     and invokes the final command handler.
    /// </summary>
    /// <typeparam name="TRequest">The type of the command, which must implement <see cref="ICommand" />.</typeparam>
    /// <param name="command">The command object to be executed.</param>
    /// <param name="ct">A cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing the <see cref="CommandResult" />.</returns>
    public Task<CommandResult> ExecuteCommandAsync<TRequest>(
        TRequest command, CancellationToken ct)
        where TRequest : ICommand
    {
        if (_dispatcherOptions.Value.RunMode == RunMode.Async)
        {
            return _backgroundTaskManager.EnqueueAsync<CommandResult>(
                async workerToken =>
                {
                    if (!ct.CanBeCanceled)
                        return await ExecuteCommandInNewScopeAsync(command, workerToken).ConfigureAwait(false);

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, workerToken);
                    return await ExecuteCommandInNewScopeAsync(command, linkedCts.Token).ConfigureAwait(false);
                },
                ct);
        }

        return ExecuteCommandImmediateAsync(command, ct);
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
        // Ensure the request has its metadata and a valid context before processing.
        InitializeRequestContext(command, provider);

        // Resolve the specific handler for this request from the selected provider.
        var handler = GetHandler(typeof(TRequest), provider);

        // Execute the request through the resolved pipeline behaviors and handler.
        return await ExecutePipelineAsync<TRequest, CommandResult>(command, handler, provider, ct).ConfigureAwait(false);
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

        // Invoke the actual handler to process the request.
        var result = await HandleRequest<TResult>(request, handler, cancellationToken).ConfigureAwait(false);

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
        return (TResult)result;
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

    private sealed class PreHandlerPriorityComparer : IComparer<IPreHandlerAttribute>
    {
        public static PreHandlerPriorityComparer Instance { get; } = new();
        private PreHandlerPriorityComparer()
        {
        }

        public int Compare(IPreHandlerAttribute? x, IPreHandlerAttribute? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var byPriority = x.PreHandlerExecutionPriority.CompareTo(y.PreHandlerExecutionPriority);
            if (byPriority != 0) return byPriority;

            return string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
        }
    }

    private sealed class PostHandlerPriorityComparer : IComparer<IPostHandlerAttribute>
    {
        public static PostHandlerPriorityComparer Instance { get; } = new();
        private PostHandlerPriorityComparer()
        {
        }

        public int Compare(IPostHandlerAttribute? x, IPostHandlerAttribute? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var byPriority = x.PostHandlerExecutionPriority.CompareTo(y.PostHandlerExecutionPriority);
            if (byPriority != 0) return byPriority;

            return string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
        }
    }

    private sealed class BehaviorPriorityComparer<TRequest, TResult> : IComparer<IPipelineBehavior<TRequest, TResult>> where TRequest : IRequest
    {
        public static BehaviorPriorityComparer<TRequest, TResult> Instance { get; } = new();

        private BehaviorPriorityComparer()
        {
        }

        public int Compare(IPipelineBehavior<TRequest, TResult>? x, IPipelineBehavior<TRequest, TResult>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x is IPrioritizedPipelineBehavior lp ? lp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var right = y is IPrioritizedPipelineBehavior rp ? rp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var byPriority = left.CompareTo(right);
            if (byPriority != 0) return byPriority;

            return string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
        }
    }
}

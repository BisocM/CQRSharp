using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.BackgroundTasks.Types;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;
using CQRSharp.Shared.Data.Interfaces.Markers.Query;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;
using CQRSharp.Shared.Data.Models.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Requests;

/// <summary>
///     Dispatches command and query requests, optionally offloading
///     execution to a background queue in Async mode, and publishing
///     notifications at each stage.
/// </summary>
public sealed class RequestDispatcher : IRequestDispatcher
{
    private readonly IBackgroundTaskQueue _backgroundTaskQueue;
    private readonly IHandlerRegistry _handlerRegistry;
    private readonly INotificationDispatcher _notificationDispatcher;
    private readonly DispatcherOptions _options;
    private readonly IRequestRegistry _requestRegistry;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    ///     Constructs a new RequestDispatcher.
    /// </summary>
    public RequestDispatcher(
        IServiceProvider serviceProvider,
        IBackgroundTaskQueue backgroundTaskQueue,
        IRequestRegistry requestRegistry,
        IHandlerRegistry handlerRegistry,
        INotificationDispatcher notificationDispatcher,
        IOptions<DispatcherOptions> options)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _backgroundTaskQueue = backgroundTaskQueue ?? throw new ArgumentNullException(nameof(backgroundTaskQueue));
        _requestRegistry = requestRegistry ?? throw new ArgumentNullException(nameof(requestRegistry));
        _handlerRegistry = handlerRegistry ?? throw new ArgumentNullException(nameof(handlerRegistry));
        _notificationDispatcher =
            notificationDispatcher ?? throw new ArgumentNullException(nameof(notificationDispatcher));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task<CommandResult> ExecuteCommand(ICommand command, CancellationToken cancellationToken = default)
    {
        switch (command)
        {
            case null:
                throw new ArgumentNullException(nameof(command));
            case IRequest req:
                InitializeRequestContext(req);
                break;
        }

        //Synchronous mode: run immediately
        if (_options.RunMode != RunMode.Async)
            return PipelineTask(cancellationToken);

        //Async mode: queue it
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = EnqueueAndWatchAsync();
        return tcs.Task;

        async Task EnqueueAndWatchAsync()
        {
            try
            {
                var result = await _backgroundTaskQueue
                    .QueueBackgroundWorkItemAsync(async ct =>
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                        try
                        {
                            var cmdResult = await PipelineTask(linkedCts.Token).ConfigureAwait(false);
                            tcs.TrySetResult(cmdResult);
                        }
                        catch (OperationCanceledException)
                        {
                            tcs.TrySetCanceled();
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }, cancellationToken)
                    .ConfigureAwait(false);

                //If not dropped on enqueue, register for drop notifications
                if (result.Result != QueueWriteResultCode.DroppedNewest &&
                    _backgroundTaskQueue is BackgroundTaskQueue concrete)
                    concrete.RegisterTaskCompletion(
                        result.SequenceNumber,
                        () => tcs.TrySetCanceled(),
                        ex => tcs.TrySetException(ex));
                else if (result.Result == QueueWriteResultCode.DroppedNewest)
                    tcs.TrySetException(new InvalidOperationException(
                        "Command execution was rejected due to a full queue."));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        async Task<CommandResult> PipelineTask(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var provider = scope.ServiceProvider;

            await _notificationDispatcher.Publish(new CommandInitiatedNotification(command), ct).ConfigureAwait(false);
            await InvokePreHandleAttributes(command, provider, ct).ConfigureAwait(false);

            var handler = GetHandler(command.GetType(), provider);
            var pipeline = BuildPipeline<CommandResult>(command, handler, provider);
            var result = await pipeline(command, ct).ConfigureAwait(false);

            await InvokePostHandleAttributes(command, provider, ct).ConfigureAwait(false);
            await _notificationDispatcher.Publish(new CommandCompletedNotification(command, result), ct)
                .ConfigureAwait(false);

            return result;
        }
    }

    /// <inheritdoc />
    public Task<TResult?> ExecuteQuery<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        switch (query)
        {
            case null:
                throw new ArgumentNullException(nameof(query));
            case IRequest req:
                InitializeRequestContext(req);
                break;
        }

        if (_options.RunMode != RunMode.Async)
            return PipelineQueryTask(cancellationToken);

        var tcs = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = EnqueueAndWatchQueryAsync();
        return tcs.Task;

        async Task EnqueueAndWatchQueryAsync()
        {
            try
            {
                var result = await _backgroundTaskQueue
                    .QueueBackgroundWorkItemAsync(async ct =>
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                        try
                        {
                            var qr = await PipelineQueryTask(linkedCts.Token).ConfigureAwait(false);
                            tcs.TrySetResult(qr);
                        }
                        catch (OperationCanceledException)
                        {
                            tcs.TrySetCanceled();
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Result != QueueWriteResultCode.DroppedNewest &&
                    _backgroundTaskQueue is BackgroundTaskQueue concrete)
                    concrete.RegisterTaskCompletion(
                        result.SequenceNumber,
                        () => tcs.TrySetCanceled(),
                        ex => tcs.TrySetException(ex));
                else if (result.Result == QueueWriteResultCode.DroppedNewest)
                    tcs.TrySetException(new InvalidOperationException(
                        "Query execution was rejected due to a full queue."));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        async Task<TResult?> PipelineQueryTask(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var provider = scope.ServiceProvider;

            await _notificationDispatcher.Publish(new QueryInitiatedNotification<TResult>(query), ct)
                .ConfigureAwait(false);
            await InvokePreHandleAttributes(query, provider, ct).ConfigureAwait(false);

            var handler = GetHandler(query.GetType(), provider);
            var pipeline = BuildPipeline<TResult>(query, handler, provider);
            var result = await pipeline(query, ct).ConfigureAwait(false);

            await InvokePostHandleAttributes(query, provider, ct).ConfigureAwait(false);
            await _notificationDispatcher.Publish(new QueryCompletedNotification<TResult>(query, result), ct)
                .ConfigureAwait(false);

            return result;
        }
    }

    #region Pipeline & Metadata Invocation

    private Func<object, CancellationToken, Task<TResult>> BuildPipeline<TResult>(
        IRequest request,
        object handler,
        IServiceProvider services)
    {
        var pipelineRegistry = services.GetRequiredService<IPipelineRegistry>();
        var requestType = request.GetType();

        var pipelineBuilder = pipelineRegistry.GetPipelineBuilder(requestType);
        if (pipelineBuilder is null)
            //No middleware pipeline; call handler directly
            return async (req, ct) =>
            {
                if (typeof(TResult) != typeof(CommandResult))
                {
                    var qr = await HandleQuery<TResult>(req, handler, ct).ConfigureAwait(false);
                    return qr;
                }

                await HandleCommand(req, handler, ct).ConfigureAwait(false);
                return (TResult)(object)CommandResult.FromSuccess();
            };

        //Wrap the final handler for the pipeline builder
        Func<CancellationToken, Task<object>> finalHandlerWrapper = async ct =>
        {
            if (typeof(TResult) != typeof(CommandResult))
            {
                var qr = await HandleQuery<TResult>(request, handler, ct).ConfigureAwait(false);
                return qr!;
            }

            await HandleCommand(request, handler, ct).ConfigureAwait(false);
            return CommandResult.FromSuccess();
        };

        return async (req, ct) =>
        {
            var raw = await pipelineBuilder(services, req, finalHandlerWrapper, ct).ConfigureAwait(false);
            return (TResult)raw;
        };
    }

    private static async Task InvokePreHandleAttributes(
        IRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var registry = serviceProvider.GetRequiredService<IRequestRegistry>();
        if (!registry.TryGetRequestMetadata(request.GetType(), out var metadata) || metadata == null)
            throw new InvalidOperationException($"No metadata for request '{request.GetType().Name}'.");

        foreach (var attr in metadata.PreHandlers)
            try
            {
                await attr.OnBeforeHandle(request, serviceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Error in pre-handle attribute '{attr.GetType().Name}' for '{request.GetType().Name}': {ex.Message}",
                    ex);
            }
    }

    private static async Task InvokePostHandleAttributes(
        IRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var registry = serviceProvider.GetRequiredService<IRequestRegistry>();
        if (!registry.TryGetRequestMetadata(request.GetType(), out var metadata) || metadata == null)
            throw new InvalidOperationException($"No metadata for request '{request.GetType().Name}'.");

        foreach (var attr in metadata.PostHandlers)
            try
            {
                await attr.OnAfterHandle(request, serviceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Error in post-handle attribute '{attr.GetType().Name}' for '{request.GetType().Name}': {ex.Message}",
                    ex);
            }
    }

    #endregion

    #region Handler Invocation & Context Initialization

    private async Task HandleCommand(object command, object handler, CancellationToken cancellationToken)
    {
        if (!_handlerRegistry.TryGetHandlerDelegate(command.GetType(), out var handlerDelegate))
            throw new InvalidOperationException($"No handler found for command '{command.GetType().Name}'.");

        ArgumentNullException.ThrowIfNull(handlerDelegate);
        await handlerDelegate(handler, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResult> HandleQuery<TResult>(object query, object handler, CancellationToken cancellationToken)
    {
        if (!_handlerRegistry.TryGetHandlerDelegate(query.GetType(), out var handlerDelegate))
            throw new InvalidOperationException($"No handler found for query '{query.GetType().Name}'.");

        ArgumentNullException.ThrowIfNull(handlerDelegate);
        var result = await handlerDelegate(handler, query, cancellationToken).ConfigureAwait(false);
        return (TResult)result;
    }

    private object GetHandler(Type requestType, IServiceProvider scopedProvider)
    {
        var handlerType = _requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        return scopedProvider.GetRequiredService(handlerType)
               ?? throw new InvalidOperationException($"Handler instance '{handlerType.Name}' not available.");
    }

    private void InitializeRequestContext(IRequest requestBase)
    {
        var registry = _serviceProvider.GetRequiredService<IRequestRegistry>();
        if (!registry.TryGetRequestMetadata(requestBase.GetType(), out var metadata) || metadata == null)
            throw new InvalidOperationException($"No metadata for request '{requestBase.GetType().Name}'.");

        requestBase.Metadata = metadata;

        if (requestBase.Context != null)
            return;

        var contextType = metadata.ContextType ?? typeof(RequestContextBase);
        var factoryRegistry = _serviceProvider.GetRequiredService<IContextFactoryRegistry>();
        var factoryObj = factoryRegistry.TryGetFactory(contextType, _serviceProvider);

        if (factoryObj is not IInternalRequestContextFactory contextFactory)
            throw new InvalidOperationException(
                $"No factory for context type '{contextType.FullName}' registered.");

        requestBase.Context = contextFactory.CreateContext(requestBase);
    }

    #endregion
}
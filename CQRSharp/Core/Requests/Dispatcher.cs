using CQRSharp.Core.BackgroundTasks;
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
///     Dispatcher responsible for sending commands to their respective handlers and managing their execution.
/// </summary>
/// <param name="serviceProvider">The service provider for dependency resolution.</param>
public sealed class Dispatcher(
    IServiceProvider serviceProvider,
    IBackgroundTaskQueue backgroundTaskQueue,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    INotificationDispatcher notificationDispatcher,
    IOptions<DispatcherOptions> options) : IDispatcher
{
    /// <inheritdoc />
    public Task<CommandResult> ExecuteCommand(ICommand command, CancellationToken cancellationToken = default)
    {
        //Ensure the command is not null.
        ArgumentNullException.ThrowIfNull(command);

        //Create the command context for this particular request.
        if (command is IRequest requestBase)
            InitializeRequestContext(requestBase);

        //Get the type of the command.
        var requestType = command.GetType();

        //Synchronous execution - await the pipeline.
        if (options.Value.RunMode != RunMode.Async)
            return PipelineTask(cancellationToken);

        //Asynchronous mode: wrap the full pipeline in a TaskCompletionSource. This will allow the user to receive a callback
        //in-line, without having to listen to the completion notification.
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = EnqueueAndWatchAsync();
        
        //Return the task that completes once the entire pipeline has finished.
        return tcs.Task;

        //Helper to do the enqueue and catch any errors while queuing
        async Task EnqueueAndWatchAsync()
        {
            try
            {
                await backgroundTaskQueue
                    .QueueBackgroundWorkItemAsync(async ct =>
                    {
                        try
                        {
                            var result = await PipelineTask(ct).ConfigureAwait(false);
                            tcs.SetResult(result);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    }, cancellationToken)
                    .ConfigureAwait(false);
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
        
        //Define the pipeline task.
        async Task<CommandResult> PipelineTask(CancellationToken ct)
        {
            using var scope = serviceProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;

            //Send off the notification for command initiation before the attributes are handled.
            await notificationDispatcher.Publish(new CommandInitiatedNotification(command), ct);

            //Invoke pre-handle attributes.
            await InvokePreHandleAttributes(command, scopedProvider, ct);

            //Retrieve the appropriate handler for the command.
            var handler = GetHandler(requestType, scopedProvider);

            //Build and execute the query pipeline.
            var pipeline = BuildPipeline<CommandResult>(command, handler, scopedProvider);
            var result = await pipeline(command, ct);

            //Send off the notification about command completion before the post-completion attributes are handled.
            await notificationDispatcher.Publish(new CommandCompletedNotification(command, result), ct);

            //Invoke post-handle attributes.
            await InvokePostHandleAttributes(command, scopedProvider, ct);

            return result;
        }
    }

    /// <inheritdoc />
    public Task<TResult?> ExecuteQuery<TResult>(IQuery<TResult> query,
        CancellationToken cancellationToken = default)
    {
        //Ensure the query is not null.
        ArgumentNullException.ThrowIfNull(query);

        //Assign a unique identifier
        if (query is IRequest requestBase)
            InitializeRequestContext(requestBase);

        //Get the type of the request.
        var requestType = query.GetType();

        //Synchronous execution - await the pipeline.
        if (options.Value.RunMode != RunMode.Async)
            return PipelineTask(cancellationToken);

        //Asynchronous mode: use TaskCompletionSource to wrap the full pipeline.
        var tcs = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = EnqueueAndWatchAsync();

        return tcs.Task;
        
        //Helper to do the enqueue and catch any errors while queuing
        async Task EnqueueAndWatchAsync()
        {
            try
            {
                await backgroundTaskQueue
                    .QueueBackgroundWorkItemAsync(async ct =>
                    {
                        try
                        {
                            var result = await PipelineTask(ct).ConfigureAwait(false);
                            tcs.SetResult(result);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    }, cancellationToken)
                    .ConfigureAwait(false);
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

        async Task<TResult?> PipelineTask(CancellationToken ct)
        {
            using var scope = serviceProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;

            //Send off the notification for query initiation before the attributes are handled.
            await notificationDispatcher.Publish(new QueryInitiatedNotification<TResult>(query), ct);

            //Invoke pre-handle attributes.
            await InvokePreHandleAttributes(query, scopedProvider, ct);

            //Get the handler for the query.
            var handler = GetHandler(requestType, scopedProvider);

            //Build and execute the query pipeline.
            var pipeline = BuildPipeline<TResult>(query, handler, scopedProvider);
            var result = await pipeline(query, ct);

            //Invoke post-handle attributes.
            await InvokePostHandleAttributes(query, scopedProvider, ct);

            //Publish the event.
            await notificationDispatcher.Publish(new QueryCompletedNotification<TResult>(query, result), ct);

            return result;
        }
    }

    /// <summary>
    ///     Builds the middleware pipeline for the command.
    /// </summary>
    /// <typeparam name="TResult">The type of the result expected.</typeparam>
    /// <param name="request">The command or query being handled.</param>
    /// <param name="handler">The handler instance.</param>
    /// <param name="services">The scoped service provider.</param>
    /// <returns>A delegate representing the pipeline.</returns>
    private Func<object, CancellationToken, Task<TResult>> BuildPipeline<TResult>(
        IRequest request,
        object handler,
        IServiceProvider services)
    {
        //Retrieve the pipeline registry that was registered in DI.
        var pipelineRegistry = services.GetRequiredService<IPipelineRegistry>();
        var requestType = request.GetType();

        //Try to get a precompiled pipeline builder for the request type.
        var pipelineBuilder = pipelineRegistry.GetPipelineBuilder(requestType);
        if (pipelineBuilder is null)
            //Fallback: If no precompiled builder exists, invoke the handler directly.
            return async (req, ct) =>
            {
                if (typeof(TResult) != typeof(CommandResult))
                {
                    var result = await HandleQuery<TResult>(req, handler, ct).ConfigureAwait(false);
                    return result;
                }
                await HandleCommand(req, handler, ct).ConfigureAwait(false);
                return (TResult)(object)CommandResult.FromSuccess();
            };

        //Construct a final handler delegate that calls the actual handler.
        //The PipelineBuilderDelegate signature is:
        //   Task<object> PipelineBuilderDelegate(IServiceProvider, object request,
        //                                         Func<CancellationToken, Task<object>> finalHandler,
        //                                         CancellationToken)
        //so we need to create a finalHandler that uses our existing handler invokers.
        Func<CancellationToken, Task<object>> finalHandlerWrapper = async ct =>
        {
            if (typeof(TResult) != typeof(CommandResult))
            {
                var qr = await HandleQuery<TResult>(request, handler, ct)
                    .ConfigureAwait(false);
                return qr;
            }

            await HandleCommand(request, handler, ct)
                .ConfigureAwait(false);
            return CommandResult.FromSuccess();
        };

        //Now return a delegate that uses the precompiled pipeline builder.
        return async (req, ct) =>
        {
            //Invoke the precompiled pipeline builder
            var raw = await pipelineBuilder(services, req, finalHandlerWrapper, ct).ConfigureAwait(false);
            return (TResult)raw;
        };
    }

    /// <summary>
    ///     Invokes all pre-handle attributes associated with the command.
    /// </summary>
    /// <param name="request">The command being handled.</param>
    /// <param name="serviceProvider">The scoped service provider.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private static async Task InvokePreHandleAttributes(IRequest request, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        //Retrieve the relevant command metadata from the registry
        var registry = serviceProvider.GetRequiredService<IRequestRegistry>();
        var success = registry.TryGetRequestMetadata(request.GetType(), out var metadata);
        if (metadata == null || !success)
            throw new InvalidOperationException($"No metadata found for command '{request.GetType().Name}'.");

        //Retrieve the attribute list
        var attributes = metadata.PreHandlers;

        //Execute them in order
        foreach (var attribute in attributes)
            try
            {
                await attribute.OnBeforeHandle(request, serviceProvider, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Error in pre-handle attribute '{attribute.GetType().Name}' " +
                    $"for command '{request.GetType().Name}': {ex.Message}", ex);
            }
    }

    /// <summary>
    ///     Invokes all post-handle attributes associated with the executable unit.
    /// </summary>
    /// <param name="request">The request that was handled.</param>
    /// <param name="serviceProvider">The scoped service provider.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private static async Task InvokePostHandleAttributes(IRequest request, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        //Retrieve the relevant command metadata from the registry
        var registry = serviceProvider.GetRequiredService<IRequestRegistry>();
        var success = registry.TryGetRequestMetadata(request.GetType(), out var metadata);
        if (metadata == null || !success)
            throw new InvalidOperationException($"No metadata found for command '{request.GetType().Name}'.");

        //Retrieve the attribute list
        var attributes = metadata.PostHandlers;

        //Execute them in order
        foreach (var attribute in attributes)
            try
            {
                await attribute.OnAfterHandle(request, serviceProvider, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Error in post-handle attribute '{attribute.GetType().Name}' " +
                    $"for command '{request.GetType().Name}': {ex.Message}", ex);
            }
    }

    /// <summary>
    ///     Handles the command by invoking its handler.
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="handler">The handler instance.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task HandleCommand(object command, object handler, CancellationToken cancellationToken)
    {
        handlerRegistry.TryGetHandlerDelegate(command.GetType(), out var handlerDelegate);
        if (handlerDelegate is null)
            throw new InvalidOperationException($"No handler found for command '{command.GetType().Name}'.");

        await handlerDelegate(handler, command, cancellationToken);
    }

    /// <summary>
    ///     Handles the query by invoking its corresponding handler and returns the result.
    /// </summary>
    /// <typeparam name="TResult">The type of the result expected from the query handler.</typeparam>
    /// <param name="query">The query to handle.</param>
    /// <param name="handler">The handler instance responsible for processing the query.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task<TResult> HandleQuery<TResult>(object query, object handler,
        CancellationToken cancellationToken)
    {
        handlerRegistry.TryGetHandlerDelegate(query.GetType(), out var handlerDelegate);
        if (handlerDelegate is null)
            throw new InvalidOperationException($"No handler found for command '{query.GetType().Name}'.");

        var queryResult = await handlerDelegate(handler, query, cancellationToken);
        return (TResult)queryResult;
    }

    /// <summary>
    ///     Retrieves the appropriate handler for the given command type.
    /// </summary>
    /// <param name="requestType">The type of the command.</param>
    /// <param name="scopedProvider">The scoped provider to resolve services.</param>
    /// <returns>The handler instance.</returns>
    private object GetHandler(Type requestType, IServiceProvider scopedProvider)
    {
        var handlerType = requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        var handler = scopedProvider.GetRequiredService(handlerType)
                      ?? throw new InvalidOperationException(
                          $"Handler of type '{handlerType.Name}' not found in the service provider.");

        return handler;
    }

    private void InitializeRequestContext(IRequest requestBase)
    {
        //Retrieve metadata for the request
        var registry = serviceProvider.GetRequiredService<IRequestRegistry>();
        if (!registry.TryGetRequestMetadata(requestBase.GetType(), out var metadata) || metadata?.ContextType == null)
        {
            //Fallback to a default context type if metadata or its ContextType is missing
            metadata = metadata ?? throw new InvalidOperationException($"No metadata found for request '{requestBase.GetType().Name}'.");
        }
        //Populate the request with its respective metadata BEFORE setting the context. The user may have opted to NOT use the built-in IRequestContextFactory.
        //In that case, we can still safely override any Metadata that the user (for ANY reason) may have populated, since the logic for its population remains static,
        //while context creation defined by the user may be completely different.
        requestBase.Metadata = metadata;
        
        //Avoid overwriting an existing context
        if (requestBase.Context != null)
            return;
    
        //Retrieve the context type from metadata (or default to a known type)
        var contextType = metadata.ContextType ?? typeof(RequestContextBase);

        //Get the factory registry
        var factoryRegistry = serviceProvider.GetRequiredService<IContextFactoryRegistry>();

        //Get the appropriate factory via the registry using the context type as key.
        var factoryObj = factoryRegistry.TryGetFactory(contextType, serviceProvider);
        if (factoryObj is not IInternalRequestContextFactory contextFactory)
        {
            throw new InvalidOperationException(
                $"No registered factory found for context type '{contextType.FullName}'. " +
                "Ensure that you have implemented and registered an IRequestContextFactory for this type.");
        }
    
        //Create the request context using the resolved factory
        requestBase.Context = contextFactory.CreateContext(requestBase);
    }
}
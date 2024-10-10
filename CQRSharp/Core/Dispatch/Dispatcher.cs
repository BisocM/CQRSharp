using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipeline;
using CQRSharp.Core.Pipeline.Attributes;
using CQRSharp.Core.Pipeline.Attributes.Markers;
using CQRSharp.Data;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Dispatch
{
    /// <summary>
    /// Dispatcher responsible for sending commands to their respective handlers and managing their execution.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="Dispatcher"/> class.
    /// </remarks>
    /// <param name="serviceProvider">The service provider for dependency resolution.</param>
    public sealed class Dispatcher(
        IServiceProvider serviceProvider,
        IBackgroundTaskQueue backgroundTaskQueue,
        IHandlerRegistry handlerRegistry,
        NotificationDispatcher eventManager,
        DispatcherOptions options) : IDispatcher
    {

        /// <inheritdoc/>
        public async Task ExecuteCommand(ICommand command, CancellationToken cancellationToken = default)
        {
            //Ensure the command is not null.
            ArgumentNullException.ThrowIfNull(command);

            //Get the type of the command.
            var requestType = command.GetType();

            if (options.RunMode == RunMode.Async)
            {
                //Asynchronous execution - fire and forget.
                backgroundTaskQueue.QueueBackgroundWorkItem(async ct => await PipelineTask(ct));
                return;
            }

            //Synchronous execution - await the pipeline.
            await PipelineTask(cancellationToken);
            return;

            //Define the pipeline task.
            async Task<Unit> PipelineTask(CancellationToken ct)
            {
                using var scope = serviceProvider.CreateScope();
                var scopedProvider = scope.ServiceProvider;

                //Invoke pre-handle attributes.
                await InvokePreHandleAttributes(command, scopedProvider, ct);

                //Retrieve the appropriate handler for the command.
                var handler = GetHandler(requestType, scopedProvider);

                //Build and execute the query pipeline.
                var pipeline = BuildPipeline<Unit>(command, handler, scopedProvider);
                var result = await pipeline(command, ct);

                //Invoke post-handle attributes.
                await InvokePostHandleAttributes(command, scopedProvider, ct);

                return result;
            }
        }

        /// <inheritdoc/>
        public async Task<TResult?> ExecuteQuery<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
        {
            //Ensure the query is not null.
            ArgumentNullException.ThrowIfNull(query);

            var requestType = query.GetType();
            var resultType = typeof(TResult);

            //Synchronous execution - await the pipeline.
            if (options.RunMode != RunMode.Async)
                return await PipelineTask(cancellationToken);

            //Asynchronous execution - fire and forget.
            backgroundTaskQueue.QueueBackgroundWorkItem(async ct => await PipelineTask(ct));
            return default;
            //Since this is fire and forget, return default, as the result will not be awaited.

            async Task<TResult> PipelineTask(CancellationToken ct)
            {
                using var scope = serviceProvider.CreateScope();
                var scopedProvider = scope.ServiceProvider;

                //Invoke pre-handle attributes.
                await InvokePreHandleAttributes(query, scopedProvider, ct);

                //Send off the notification for query initiation before the attributes are handled.
                await eventManager.Publish(new QueryInitiatedNotification<TResult>(query), ct);

                //Get the handler for the query.
                var handler = GetHandler(requestType, scopedProvider);

                //Build and execute the query pipeline.
                var pipeline = BuildPipeline<TResult>(query, handler, scopedProvider);
                var result = await pipeline(query, ct);

                //Invoke post-handle attributes.
                await InvokePostHandleAttributes(query, scopedProvider, ct);

                //Publish the event.
                await eventManager.Publish(new QueryCompletedNotification<TResult>(query, result), ct);

                return result;
            }
        }

        /// <summary>
        /// Builds the middleware pipeline for the command.
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
            var requestType = request.GetType();
            var resultType = typeof(TResult);

            //Retrieve all pipeline behaviors registered in the container.
            var behaviors = services
                .GetServices(typeof(IPipelineBehavior<,>).MakeGenericType(requestType, resultType))//Populate the generic type arguments in the pipeline behavior.
                .Cast<dynamic>()
                .Reverse() //Reverse to maintain the correct order of execution.
                .ToList();

            //The final handler delegate.
            Func<object, CancellationToken, Task<TResult>> handlerDelegate = async (req, ct) =>
            {
                //Determine whether the request is a command or a query.
                if (typeof(TResult) == typeof(Unit))
                {
                    await HandleCommand(req, handler, ct);
                    return (TResult)(object)Unit.Success;
                }
                else
                    return await HandleQuery<TResult>(req, handler, ct);
            };

            //Check if the request is exempt from any behaviors.
            var exemptionAttribute = request.GetType().GetCustomAttribute<PipelineExemptionAttribute>();

            //Wrap the handler with the pipeline behaviors.
            foreach (var behavior in behaviors)
            {
                Type behaviorType = behavior.GetType();

                //Skip the behavior if the request is exempt.
                if (exemptionAttribute != null && exemptionAttribute.ExemptedPipeline == behaviorType.GetGenericTypeDefinition())
                    continue;

                var next = handlerDelegate;
                handlerDelegate = (req, ct) =>
                {
                    //Invoke the behavior's Handle method.
                    return behavior.Handle((dynamic)req, ct, (Func<CancellationToken, Task<TResult>>)((cancellationToken) => next(req, cancellationToken)));
                };
            }

            return handlerDelegate;
        }

        /// <summary>
        /// Invokes all pre-handle attributes associated with the command.
        /// </summary>
        /// <param name="command">The command being handled.</param>
        /// <param name="serviceProvider">The scoped service provider.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        private async Task InvokePreHandleAttributes(IRequest command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //Retrieve all attributes implementing IPreCommandAttribute.
            var attributes = command.GetType().GetCustomAttributes(true)
                .OfType<IPreHandlerAttribute>()
                .OrderBy(a => a.Priority);

            foreach (var attribute in attributes)
            {
                try
                {
                    //Invoke the OnBeforeHandle method of the attribute.
                    await attribute.OnBeforeHandle(command, serviceProvider, cancellationToken);
                }
                catch (Exception ex)
                {
                    //Wrap exceptions with additional context.
                    throw new InvalidOperationException($"Error in pre-handle attribute '{attribute.GetType().Name}' for command '{command.GetType().Name}': {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Invokes all post-handle attributes associated with the executable unit.
        /// </summary>
        /// <param name="request">The request that was handled.</param>
        /// <param name="serviceProvider">The scoped service provider.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        private async Task InvokePostHandleAttributes(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //Retrieve all attributes implementing IPostCommandAttribute.
            var attributes = request.GetType().GetCustomAttributes(true)
                .OfType<IPostHandlerAttribute>()
                .OrderBy(a => a.Priority);

            foreach (var attribute in attributes)
            {
                try
                {
                    //Invoke the OnAfterHandle method of the attribute.
                    await attribute.OnAfterHandle(request, serviceProvider, cancellationToken);
                }
                catch (Exception ex)
                {
                    //Wrap exceptions with additional context.
                    throw new InvalidOperationException($"Error in post-handle attribute '{attribute.GetType().Name}' for command '{request.GetType().Name}': {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Handles the command by invoking its handler.
        /// </summary>
        /// <param name="command">The command to handle.</param>
        /// <param name="handler">The handler instance.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        private async Task HandleCommand(object command, object handler, CancellationToken cancellationToken)
        {
            //Get the 'Handle' method from the handler.
            var method = handler.GetType().GetMethod("Handle")
                ?? throw new InvalidOperationException("Handler does not have a 'Handle' method.");

            //Invoke the 'Handle' method with the command and cancellation token.
            var result = method.Invoke(handler, [command, cancellationToken]);

            if (result is Task task)
            {
                //Await the task if the result is a Task.
                await task;
            }
            else
            {
                //Throw an exception if the handler did not return a Task.
                throw new InvalidOperationException("Handler did not return a Task.");
            }
        }

        /// <summary>
        /// Handles the query by invoking its corresponding handler and returns the result.
        /// </summary>
        /// <typeparam name="TResult">The type of the result expected from the query handler.</typeparam>
        /// <param name="query">The query to handle.</param>
        /// <param name="handler">The handler instance responsible for processing the query.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        private async Task<TResult> HandleQuery<TResult>(object query, object handler, CancellationToken cancellationToken)
        {
            var method = handler.GetType().GetMethod("Handle")
                ?? throw new InvalidOperationException("Handler does not have a 'Handle' method.");

            var result = method.Invoke(handler, [query, cancellationToken]);

            if (result is Task<TResult> task)
                return await task;

            throw new InvalidOperationException($"Handler did not return a Task<{typeof(TResult).Name}>.");
        }


        /// <summary>
        /// Retrieves the appropriate handler for the given command type.
        /// </summary>
        /// <param name="requestType">The type of the command.</param>
        /// <param name="scopedProvider">The scoped provider to resolve services.</param>
        /// <returns>The handler instance.</returns>
        private object GetHandler(Type requestType, IServiceProvider scopedProvider)
        {
            var handlerType = handlerRegistry.GetHandlerType(requestType)
                              ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

            var handler = scopedProvider.GetRequiredService(handlerType)
                          ?? throw new InvalidOperationException($"Handler of type '{handlerType.Name}' not found in the service provider.");

            return handler;
        }
    }
}
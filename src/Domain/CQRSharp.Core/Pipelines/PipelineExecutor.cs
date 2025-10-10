using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Orchestrates the execution of commands and queries through a series of behaviors (the pipeline).
///     This is the central component that connects a request to its handler, wrapping it with cross-cutting concerns
///     like logging, transactions, validation, etc.
/// </summary>
/// <param name="serviceProvider">The root service provider to create scoped dependencies.</param>
/// <param name="requestRegistry">The registry for request metadata.</param>
/// <param name="handlerRegistry">The registry for compiled handler invokers.</param>
/// <param name="contextFactoryRegistry">The registry for request context factories.</param>
public sealed class PipelineExecutor(
    IServiceProvider serviceProvider,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IContextFactoryRegistry contextFactoryRegistry) : IPipelineExecutor
{
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
    public async Task<TResult> ExecuteQueryAsync<TRequest, TResult>(
        TRequest query, CancellationToken ct)
        where TRequest : IQuery<TResult>
    {
        // Ensure the request has its metadata and a valid context before processing.
        InitializeRequestContext(query);

        // Create a new DI scope for the request to ensure services are scoped correctly (e.g., DbContext, UnitOfWork).
        await using var scope = serviceProvider.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        // Resolve the specific handler for this request from the scoped provider.
        var handler = GetHandler(typeof(TRequest), provider);

        // Construct the full pipeline of behaviors ending with the handler itself.
        var pipeline = BuildPipeline<TRequest, TResult>(handler, provider);

        // Execute the constructed pipeline.
        var result = await pipeline(query, ct).ConfigureAwait(false);
        return result;
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
    public async Task<CommandResult> ExecuteCommandAsync<TRequest>(
        TRequest command, CancellationToken ct)
        where TRequest : ICommand
    {
        // Ensure the request has its metadata and a valid context before processing.
        InitializeRequestContext(command);

        // Create a new DI scope for the request.
        await using var scope = serviceProvider.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        // Resolve the specific handler for this request from the scoped provider.
        var handler = GetHandler(typeof(TRequest), provider);

        // Construct the full pipeline of behaviors ending with the handler.
        var pipeline = BuildPipeline<TRequest, CommandResult>(handler, provider);

        // Execute the constructed pipeline.
        var result = await pipeline(command, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    ///     Constructs the request processing pipeline for a specific request type.
    ///     <para>
    ///         This method resolves all registered <see cref="IPipelineBehavior{TRequest, TResult}" /> instances
    ///         directly from the current request's scoped service provider. It then sorts these instances in-memory
    ///         based on the <see cref="PipelinePriorityAttribute" /> to establish the correct execution order.
    ///         This approach is efficient and simplifies DI configuration, as behaviors only need to be registered once.
    ///     </para>
    /// </summary>
    /// <typeparam name="TRequest">The type of the request entering the pipeline.</typeparam>
    /// <typeparam name="TResult">The result type of the request.</typeparam>
    /// <param name="handler">The resolved handler instance for the request.</param>
    /// <param name="services">The scoped service provider for resolving dependencies.</param>
    /// <returns>A delegate representing the entire executable pipeline.</returns>
    private Func<TRequest, CancellationToken, Task<TResult>> BuildPipeline<TRequest, TResult>(
        object handler,
        IServiceProvider services) where TRequest : IRequest
    {
        // Resolve all registered pipeline behavior INSTANCES from the current request's DI scope.
        // This ensures that any scoped dependencies within the behaviors are correctly managed.
        var behaviors = services.GetServices<IPipelineBehavior<TRequest, TResult>>()
            // Order the resolved instances based on the PipelinePriorityAttribute. Lower numbers execute first.
            .OrderBy(b => b.GetType().GetCustomAttributes(typeof(PipelinePriorityAttribute), true)
                .Cast<PipelinePriorityAttribute>()
                .FirstOrDefault()?.Priority ?? PipelinePriorityAttribute.DefaultPriority)
            // The list is reversed to facilitate the Aggregate call, which builds the chain from the inside out.
            .Reverse();

        // Chain the behaviors together using Aggregate.
        // This creates a nested delegate structure (Chain of Responsibility pattern) where each behavior
        // calls the 'next' one in the sequence, eventually calling the 'finalAction'.
        var pipeline = behaviors.Aggregate(
            (Func<TRequest, CancellationToken, Task<TResult>>)FinalAction,
            (next, behavior) => (req, ct) => behavior.Handle(req, cancellationToken => next(req, cancellationToken), ct)
        );

        return pipeline;

        // This is the final action in the pipeline, responsible for executing the core business logic.
        // It also handles invoking pre- / post-handler attributes and publishing start/completion notifications.
        async Task<TResult> FinalAction(TRequest req, CancellationToken ct)
        {
            var notificationDispatcher = services.GetRequiredService<INotificationDispatcher>();

            switch (req)
            {
                // Publish notifications to signal the start of command/query handling.
                case ICommand cmd:
                    await notificationDispatcher.Publish(new CommandInitiatedNotification(cmd), ct);
                    break;
                case IQuery<TResult> qry:
                    await notificationDispatcher.Publish(new QueryInitiatedNotification<TResult>(qry), ct);
                    break;
            }

            // Execute any pre-handler logic defined via attributes on the request class.
            await InvokePreHandleAttributes(req, services, ct);

            // Invoke the actual handler to process the request.
            var result = await HandleRequest<TResult>(req, handler, ct);

            // Execute any post-handler logic defined via attributes.
            await InvokePostHandleAttributes(req, services, ct);

            switch (req)
            {
                // Publish notifications to signal the completion of command/query handling.
                case ICommand cmdResult:
                    await notificationDispatcher.Publish(new CommandCompletedNotification(cmdResult, (result as CommandResult)!), ct);
                    break;
                case IQuery<TResult> qryResult:
                    await notificationDispatcher.Publish(new QueryCompletedNotification<TResult>(qryResult, result), ct);
                    break;
            }

            return result;
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
        if (!handlerRegistry.TryGetHandlerDelegate(request.GetType(), out var handlerDelegate))
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
        // Attributes are executed in order of their specified priority.
        foreach (var attr in request.Metadata.PreHandlers.OrderBy(p => p.PreHandlerExecutionPriority))
            await attr.OnBeforeHandle(request, sp, ct).ConfigureAwait(false);
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
        // Attributes are executed in order of their specified priority.
        foreach (var attr in request.Metadata.PostHandlers.OrderBy(p => p.PostHandlerExecutionPriority))
            await attr.OnAfterHandle(request, sp, ct).ConfigureAwait(false);
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
    private void InitializeRequestContext(IRequest requestBase)
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
        var factoryObj = contextFactoryRegistry.TryGetFactory(contextType, serviceProvider);

        if (factoryObj is not IInternalRequestContextFactory contextFactory)
            throw new InvalidOperationException($"No factory for context type '{contextType.FullName}' registered.");

        // Create and assign the context to the request.
        requestBase.Context = contextFactory.CreateContext(requestBase);
    }
}
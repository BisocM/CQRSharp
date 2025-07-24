using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

public sealed class PipelineExecutor(
    IServiceProvider serviceProvider,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IPipelineRegistry pipelineRegistry,
    IContextFactoryRegistry contextFactoryRegistry)
{
    public async Task<TResult> ExecuteQueryPipelineAsync<TRequest, TResult>(
        TRequest query, CancellationToken ct)
        where TRequest : IQuery<TResult>
    {
        InitializeRequestContext(query);

        using var scope = serviceProvider.CreateScope();
        var provider = scope.ServiceProvider;
        var notificationDispatcher = provider.GetRequiredService<INotificationDispatcher>();

        await notificationDispatcher.Publish(new QueryInitiatedNotification<TResult>(query), ct).ConfigureAwait(false);
        await InvokePreHandleAttributes(query, provider, ct).ConfigureAwait(false);

        var handler = GetHandler(typeof(TRequest), provider);
        var pipeline = BuildPipeline<TResult>(query, handler, provider);
        var result = await pipeline(query, ct).ConfigureAwait(false);

        await InvokePostHandleAttributes(query, provider, ct).ConfigureAwait(false);
        await notificationDispatcher.Publish(new QueryCompletedNotification<TResult>(query, result), ct).ConfigureAwait(false);

        return result;
    }

    public async Task<CommandResult> ExecuteCommandPipelineAsync<TRequest>(
        TRequest command, CancellationToken ct)
        where TRequest : ICommand
    {
        InitializeRequestContext(command);

        using var scope = serviceProvider.CreateScope();
        var provider = scope.ServiceProvider;
        var notificationDispatcher = provider.GetRequiredService<INotificationDispatcher>();

        await notificationDispatcher.Publish(new CommandInitiatedNotification(command), ct).ConfigureAwait(false);
        await InvokePreHandleAttributes(command, provider, ct).ConfigureAwait(false);

        var handler = GetHandler(typeof(TRequest), provider);
        var pipeline = BuildPipeline<CommandResult>(command, handler, provider);
        var result = await pipeline(command, ct).ConfigureAwait(false);

        await InvokePostHandleAttributes(command, provider, ct).ConfigureAwait(false);
        await notificationDispatcher.Publish(new CommandCompletedNotification(command, result), ct).ConfigureAwait(false);

        return result;
    }

    private Func<object, CancellationToken, Task<TResult>> BuildPipeline<TResult>(
        IRequest request,
        object handler,
        IServiceProvider services)
    {
        var requestType = request.GetType();
        var pipelineBuilder = pipelineRegistry.GetPipelineBuilder(requestType);

        if (pipelineBuilder is null)
        {
            return (req, ct) => HandleRequest<TResult>(req, handler, ct);
        }

        Func<CancellationToken, Task<object>> finalHandlerWrapper = async ct =>
        {
            var result = await HandleRequest<TResult>(request, handler, ct).ConfigureAwait(false);
            return result!;
        };

        return async (req, ct) =>
        {
            var raw = await pipelineBuilder(services, req, finalHandlerWrapper, ct).ConfigureAwait(false);
            return (TResult)raw;
        };
    }

    private async Task<TResult> HandleRequest<TResult>(object request, object handler, CancellationToken cancellationToken)
    {
        if (!handlerRegistry.TryGetHandlerDelegate(request.GetType(), out var handlerDelegate))
        {
            throw new InvalidOperationException($"No handler delegate found for request '{request.GetType().Name}'.");
        }

        ArgumentNullException.ThrowIfNull(handlerDelegate);
        var result = await handlerDelegate(handler, request, cancellationToken).ConfigureAwait(false);
        return (TResult)result;
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
        {
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
        {
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
    }

    private object GetHandler(Type requestType, IServiceProvider scopedProvider)
    {
        var handlerType = requestRegistry.TryGetHandlerType(requestType)
                          ?? throw new InvalidOperationException($"Handler for '{requestType.Name}' not found.");

        return scopedProvider.GetRequiredService(handlerType)
               ?? throw new InvalidOperationException($"Handler instance '{handlerType.Name}' not available.");
    }

    private void InitializeRequestContext(IRequest requestBase)
    {
        if (!requestRegistry.TryGetRequestMetadata(requestBase.GetType(), out var metadata) || metadata == null)
            throw new InvalidOperationException($"No metadata for request '{requestBase.GetType().Name}'.");

        requestBase.Metadata = metadata;

        if (requestBase.Context != null)
        {
            return;
        }

        var contextType = metadata.ContextType ?? typeof(RequestContextBase);
        var factoryObj = contextFactoryRegistry.TryGetFactory(contextType, serviceProvider);

        if (factoryObj is not IInternalRequestContextFactory contextFactory)
            throw new InvalidOperationException(
                $"No factory for context type '{contextType.FullName}' registered.");

        requestBase.Context = contextFactory.CreateContext(requestBase);
    }
}
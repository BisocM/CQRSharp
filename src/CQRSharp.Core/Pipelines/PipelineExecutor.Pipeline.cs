using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Background.Outbox.Types;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

public sealed partial class PipelineExecutor
{
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

        // A failed attempt's buffered notifications describe work that did not happen: remember where this attempt
        // starts so they can be discarded without touching an outer request's (or an earlier retry attempt's) buffer.
        var outbox = _outboxEnabled ? services.GetService<IOutbox>() as Outbox : null;
        var outboxMark = outbox?.Count ?? 0;

        // Pre-handlers and the handler share one failure path: once *Initiated is published, a throw from either must
        // still produce the terminal *Failed notification and reach the outcome-aware post-handlers.
        TResult result;
        try
        {
            // Execute any pre-handler logic defined via attributes on the request class.
            await InvokePreHandleAttributes(request, services, cancellationToken).ConfigureAwait(false);

            // Invoke the actual handler to process the request.
            result = await HandleRequest<TResult>(request, handler, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outbox?.TruncateTo(outboxMark);
            await PublishFailedIsolatedAsync<TResult>(notificationDispatcher, request, ex, cancellationToken).ConfigureAwait(false);
            await InvokePostHandleAttributesIsolated(request, RequestOutcome.FromException(ex), services, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Execute any post-handler logic defined via attributes, handing them the successful result as the outcome.
        await InvokePostHandleAttributes(request, RequestOutcome.FromResult(result), services, cancellationToken).ConfigureAwait(false);

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

    // Publishes the terminal *Failed notification without letting a faulting subscriber (or the request's already
    // cancelled token) replace the exception that is about to propagate: the original failure must always surface.
    private static async Task PublishFailedIsolatedAsync<TResult>(
        INotificationDispatcher notificationDispatcher,
        IRequest request,
        Exception failure,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request)
            {
                case ICommand cmdFailed:
                    await notificationDispatcher.Publish(new CommandFailedNotification(cmdFailed, failure), cancellationToken).ConfigureAwait(false);
                    break;
                case IQuery<TResult> qryFailed:
                    await notificationDispatcher.Publish(new QueryFailedNotification<TResult>(qryFailed, failure), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch
        {
            // Swallow: see above.
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
}

using CQRSharp.Pipelines;
using CQRSharp.Core.Background.Outbox.Types;
using CQRSharp.Core.Notifications;
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
    /// <param name="plan">The cached per-provider plan for this request type.</param>
    /// <param name="request">The request instance entering the pipeline.</param>
    /// <param name="handler">The resolved handler instance for the request.</param>
    /// <param name="services">The scoped service provider for resolving dependencies.</param>
    /// <param name="cancellationToken">A cancellation token for the request.</param>
    private Task<TResult> ExecutePipelineAsync<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        // The provider has no IPipelineBehavior<TRequest, TResult> registration at all: skip the enumerable resolution.
        if (!plan.MayHaveBehaviors)
            return ExecuteFinalActionAsync(plan, request, handler, services, cancellationToken);

        // GetServices returns a freshly-allocated array per resolution (Microsoft DI), so the in-place exemption
        // filter and priority sort below are concurrency-safe: each dispatch owns its array and never mutates a shared
        // or cached collection. (Verified by the hot-path contention stress tests.) Do not change this to a cached array.
        var resolved = services.GetServices<IPipelineBehavior<TRequest, TResult>>();
        var behaviors = resolved as IPipelineBehavior<TRequest, TResult>[] ?? resolved.ToArray();

        // Filter out behaviors that are exempted by request metadata (supports closed and open generic exemptions).
        var exemptions = plan.Metadata.PipelineExemptions;
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
            return ExecuteFinalActionAsync(plan, request, handler, services, cancellationToken);

        // The chains live in their own methods: a closure is allocated where its captured variables are declared, so
        // keeping the lambdas here would charge every behavior-less dispatch for one it never uses.
        return behaviorCount == 1
            ? RunSingleBehavior(plan, behaviors[0], request, handler, services, cancellationToken)
            : RunBehaviorChain(plan, behaviors, behaviorCount, request, handler, services, cancellationToken);
    }

    // The common single-behavior chain needs one delegate, not the general recursive closure.
    private Task<TResult> RunSingleBehavior<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        IPipelineBehavior<TRequest, TResult> behavior,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        return behavior.Handle(
            request,
            nextToken => ExecuteFinalActionAsync(plan, request, handler, services, nextToken),
            cancellationToken);
    }

    private Task<TResult> RunBehaviorChain<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        IPipelineBehavior<TRequest, TResult>[] behaviors,
        int behaviorCount,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        Array.Sort(behaviors, 0, behaviorCount, BehaviorPriorityComparer<TRequest, TResult>.Instance);

        return InvokeBehavior(0, cancellationToken);

        Task<TResult> InvokeBehavior(int index, CancellationToken ct)
        {
            if (index >= behaviorCount)
                return ExecuteFinalActionAsync(plan, request, handler, services, ct);

            var behavior = behaviors[index];
            return behavior.Handle(
                request,
                nextToken => InvokeBehavior(index + 1, nextToken),
                ct);
        }
    }

    private Task<TResult> ExecuteFinalActionAsync<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        // Nothing brackets the handler — no interceptors, no lifecycle subscribers, no outbox buffer to roll back — so
        // the final action IS the handler call: return its task as-is instead of wrapping it in a state machine.
        if (plan.IsBareHandler && !_outboxEnabled)
            return InvokeHandler(plan, request, handler, cancellationToken);

        return ExecuteBracketedFinalActionAsync(plan, request, handler, services, cancellationToken);
    }

    private async Task<TResult> ExecuteBracketedFinalActionAsync<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        // Lifecycle notifications are skipped only when the provider can prove nothing subscribes to them.
        var notificationDispatcher = plan.MayHaveLifecycleSubscribers
            ? services.GetRequiredService<INotificationDispatcher>()
            : null;

        if (notificationDispatcher is not null)
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
            var preHandlers = plan.PreHandlers;
            for (var i = 0; i < preHandlers.Length; i++)
                await preHandlers[i].OnBeforeHandle(request, services, cancellationToken).ConfigureAwait(false);

            // Invoke the actual handler to process the request.
            result = await InvokeHandler(plan, request, handler, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outbox?.TruncateTo(outboxMark);
            if (notificationDispatcher is not null)
                await PublishFailedIsolatedAsync<TResult>(notificationDispatcher, request, ex, cancellationToken).ConfigureAwait(false);
            await InvokePostHandlersIsolated(plan.PostHandlers, request, RequestOutcome.FromException(ex), services, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Execute any post-handler logic defined via attributes, handing them the successful result as the outcome.
        if (plan.PostHandlers.Length > 0)
            await InvokePostHandlers(plan.PostHandlers, request, RequestOutcome.FromResult(result), services, cancellationToken).ConfigureAwait(false);

        if (notificationDispatcher is not null)
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
    ///     Invokes the handler. With the generator's typed invoker this returns the handler's own task: no boxing, and no
    ///     state machine unless the handler is genuinely asynchronous (where a null result still has to be checked).
    /// </summary>
    private Task<TResult> InvokeHandler<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        TRequest request,
        object handler,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        Task<TResult>? task;
        try
        {
            task = plan.Invoker(handler, request, cancellationToken);
        }
        catch (Exception ex)
        {
            // A handler that throws before its first await: surface it through the task, as an async handler would.
            return Task.FromException<TResult>(ex);
        }

        if (task is null)
            return Task.FromException<TResult>(NullResult(request));

        if (!task.IsCompletedSuccessfully)
            return AwaitAndValidate(task, request);

        return IsInvalidNull(task.Result, request) ? Task.FromException<TResult>(NullResult(request)) : task;

        static async Task<TResult> AwaitAndValidate(Task<TResult> pending, TRequest awaitedRequest)
        {
            var result = await pending.ConfigureAwait(false);
            return IsInvalidNull(result, awaitedRequest) ? throw NullResult(awaitedRequest) : result;
        }
    }

    // A command must produce a CommandResult and a non-nullable value-type result cannot be null; a query may
    // legitimately return null for a nullable/reference result.
    private static bool IsInvalidNull<TResult>(TResult result, IRequest request)
        => result is null && (request is ICommand || default(TResult) is not null);

    private static InvalidOperationException NullResult(IRequest request)
        => new($"Handler returned null for request '{request.GetType().Name}'.");
}

using System.Runtime.ExceptionServices;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

internal sealed partial class PipelineExecutor
{
    /// <summary>
    ///     Executes the request processing pipeline for a specific request type: the request's
    ///     <see cref="IPipelineBehavior{TRequest, TResult}" />s from the current scope, less the ones its metadata exempts,
    ///     in priority order around the final action.
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

        var behaviors = ResolveBehaviors<IPipelineBehavior<TRequest, TResult>>(plan, services, out var behaviorCount);
        if (behaviorCount == 0)
            return ExecuteFinalActionAsync(plan, request, handler, services, cancellationToken);

        // The chains live in their own methods: a closure is allocated where its captured variables are declared, so
        // keeping the lambdas here would charge every behavior-less dispatch for one it never uses.
        return behaviorCount == 1
            ? RunSingleBehavior(plan, behaviors[0], request, handler, services, cancellationToken)
            : RunBehaviorChain(plan, behaviors, behaviorCount, request, handler, services, cancellationToken);
    }

    /// <summary>
    ///     Resolves a request's behaviors into an array this dispatch owns, with the ones its metadata exempts compacted
    ///     away; <paramref name="count" /> is how many of its leading entries are in use.
    /// </summary>
    private static TBehavior[] ResolveBehaviors<TBehavior>(RequestPlanBase plan, IServiceProvider services, out int count)
        where TBehavior : class
    {
        // The in-place exemption filter below and the priority sort after it need an array this dispatch owns, which is
        // what ResolveAll hands back.
        var behaviors = PipelineBehaviors.ResolveAll<TBehavior>(services, plan.UsesClosedBehaviors, plan.MergesDiscoveredBehaviors);

        count = behaviors.Length;
        var exemptions = plan.Metadata.PipelineExemptions;
        if (exemptions is not { Length: > 0 } || count == 0) return behaviors;

        var write = 0;
        for (var read = 0; read < count; read++)
        {
            var behavior = behaviors[read];
            if (PipelineExemptions.IsExempted(behavior.GetType(), exemptions)) continue;
            behaviors[write++] = behavior;
        }

        count = write;
        return behaviors;
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

    // Once *Initiated is published the request always ends in exactly one terminal notification, whichever stage fails:
    // *Completed after the handler and every post-handler succeeded, *Failed otherwise (cancellation and timeouts
    // included), and the outcome-aware post-handlers run on both paths.
    private async Task<TResult> ExecuteBracketedFinalActionAsync<TRequest, TResult>(
        RequestPlan<TRequest, TResult> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken) where TRequest : IRequest
    {
        // Lifecycle notifications are skipped only when the provider can prove nothing subscribes to them.
        var publishesLifecycle = plan.MayHaveLifecycleSubscribers;

        var kind = RequestKindOf<TRequest, TResult>.Value;
        if (publishesLifecycle)
            switch (kind)
            {
                // Every command, a value-returning one (ICommand<T>, dispatched with a CommandResult<T>) included.
                case RequestKind.Command:
                    await _notifications.Publish(services, new CommandInitiatedNotification((ICommandMarker)request), cancellationToken).ConfigureAwait(false);
                    break;
                case RequestKind.Query:
                    await _notifications.Publish(services, new QueryInitiatedNotification<TResult>((IQuery<TResult>)request), cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }

        // A failed attempt's buffered notifications describe work that did not happen: this attempt owns what it
        // buffers, so they can be discarded without touching an outer request's (or an earlier retry attempt's).
        var attempt = OutboxAttempt.Begin(_outboxEnabled, services);

        TResult result;
        try
        {
            var preHandlers = plan.PreHandlers;
            for (var i = 0; i < preHandlers.Length; i++)
                await preHandlers[i].OnBeforeHandle(request, services, cancellationToken).ConfigureAwait(false);

            result = await InvokeHandler(plan, request, handler, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            attempt.Fail();
            if (publishesLifecycle)
                await PublishFailedAsync<TRequest, TResult>(services, request, ex).ConfigureAwait(false);
            await InvokePostHandlersAsync<TRequest>(plan.PostHandlers, request, RequestOutcome.FromException(ex), services, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        // The handler succeeded, but the request does not until every post-handler has: the first that fails is the
        // failure the caller sees, the notifications this attempt buffered describe work the caller treats as not done,
        // and the lifecycle ends as for any other failure.
        if (plan.PostHandlers.Length > 0 &&
            await InvokePostHandlersAsync<TRequest>(plan.PostHandlers, request, RequestOutcome.FromResult(result), services, cancellationToken)
                .ConfigureAwait(false) is { } postHandlerFailure)
        {
            attempt.Fail();
            if (publishesLifecycle)
                await PublishFailedAsync<TRequest, TResult>(services, request, postHandlerFailure).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(postHandlerFailure).Throw();
        }

        if (publishesLifecycle)
            try
            {
                switch (kind)
                {
                    // The kind guarantees a CommandResult (a value-returning command's CommandResult<T> derives from it).
                    case RequestKind.Command:
                        await _notifications.Publish(services, new CommandCompletedNotification((ICommandMarker)request, (CommandResult)(object)result!), cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case RequestKind.Query:
                        await _notifications.Publish(services, new QueryCompletedNotification<TResult>((IQuery<TResult>)request, result), cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch
            {
                // *Completed is already the terminal notification, so no *Failed follows it; but the caller sees this
                // failure (and a retry runs the handler again), so the attempt's notifications go the way of any failed
                // attempt's.
                attempt.Fail();
                throw;
            }

        return result;
    }

    private Task PublishFailedAsync<TRequest, TResult>(IServiceProvider services, TRequest request, Exception failure)
        where TRequest : IRequest
        => RequestKindOf<TRequest, TResult>.Value switch
        {
            RequestKind.Command => PublishTerminalFailureAsync<TRequest, CommandFailedNotification>(
                services, new CommandFailedNotification((ICommandMarker)request, failure)),
            RequestKind.Query => PublishTerminalFailureAsync<TRequest, QueryFailedNotification<TResult>>(
                services, new QueryFailedNotification<TResult>((IQuery<TResult>)request, failure)),
            _ => Task.CompletedTask
        };

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

    // A command must produce a CommandResult (ICommand<T> dispatches as a query whose result is CommandResult<T>, so
    // the result type is checked too) and a non-nullable value-type result cannot be null; a query may legitimately
    // return null for a nullable/reference result.
    private static bool IsInvalidNull<TResult>(TResult result, IRequest request)
        => result is null && (request is ICommand || default(TResult) is not null || typeof(CommandResult).IsAssignableFrom(typeof(TResult)));

    private static InvalidOperationException NullResult(IRequest request)
        => new($"Handler returned null for request '{request.GetType().Name}'.");
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

internal sealed partial class PipelineExecutor
{
    // The streaming counterpart of the command/query fast path: in the current scope, untraced, with the outbox off and
    // nothing registered around the handler, the stream the caller enumerates IS the handler's stream — no wrapping
    // iterators, no per-item hop. Returns null when any of that does not hold and the full path is needed.
    private IAsyncEnumerable<TItem>? TryExecuteBareStream<TRequest, TItem>(TRequest request, CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        if (_outboxEnabled || CqrsActivitySource.Instance.HasListeners() || _metrics.RequestDuration.Enabled) return null;

        try
        {
            var plan = _plans.GetStream<TRequest, TItem>();
            if (!plan.IsBareHandler || plan.MayHaveBehaviors) return null;

            // No behavior is registered for it, so a marker of it that needs one is not honored.
            plan.MarkerCheck?.Verify(ReadOnlySpan<object>.Empty);

            // A custom factory may hydrate its context asynchronously, which needs an await before the handler runs:
            // only the built-in context, which is built synchronously, can take the bare path.
            if (!plan.UsesDefaultContextFactory) return null;

            var contextReady = InitializeRequestContext(plan, request, ct);
            Debug.Assert(contextReady.IsCompletedSuccessfully);

            var handler = _services.GetRequiredService(plan.HandlerType);
            return plan.Invoker(handler, request, ct) ?? throw NullStream(request);
        }
        catch (Exception ex)
        {
            // Dispatching a stream never throws; a failure surfaces when the stream is enumerated, as on the full path.
            return Throwing<TItem>(ex);
        }
    }

#pragma warning disable CS1998 // deliberately synchronous: the iterator exists only to defer the throw to enumeration
    private static async IAsyncEnumerable<TItem> Throwing<TItem>(Exception failure)
    {
        ExceptionDispatchInfo.Capture(failure).Throw();
        yield break;
    }
#pragma warning restore CS1998

    // The full stream path. The dispatch token and the token the consumer enumerates with are linked by the compiler
    // ([EnumeratorCancellation]); a stream in a scope of its own opens the scope when enumeration starts.
    private async IAsyncEnumerable<TItem> ExecuteStreamInProviderCore<TRequest, TItem>(
        TRequest request,
        bool inOwnScope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        // Declared first so it is disposed last, once the outbox and the metric below have been settled.
        var ownScope = inOwnScope ? _scopeFactory.CreateAsyncScope() : (AsyncServiceScope?)null;
        try
        {
            var provider = ownScope?.ServiceProvider ?? _services;
            using var activity = CqrsActivitySource.StartRequest(StreamActivityName, typeof(TRequest));

            // Same outbox ownership as the command/query path: store what the stream buffered when it completes, discard
            // it when it faults or the consumer stops enumerating early (the finally below runs on iterator disposal too).
            // What its pipeline buffers is attributed to it - and not what its consumer publishes between items: the owner
            // is made current again around every step of the pipeline below, since the consumer's code runs between steps.
            var outboxScope = _outboxEnabled ? RequestOutboxScope.Begin(provider) : null;
            var outboxOwner = outboxScope?.Owner;
            var outboxSettled = false;
            var metered = _metrics.RequestDuration.Enabled;
            var startedAt = metered ? _timeProvider.GetTimestamp() : 0L;
            var succeeded = false;
            var abandoned = false;
            try
            {
                IAsyncEnumerable<TItem> pipeline;
                try
                {
                    // Context init is awaited (the factory may hydrate asynchronously) before the handler is resolved and
                    // the stream pipeline is built, so the context is fully populated by the time the first item is produced.
                    // It is built from the caller's scope, not from a scope of the stream's own, on the enumerating flow.
                    var plan = _plans.GetStream<TRequest, TItem>();
                    await InitializeRequestContext(plan, request, cancellationToken).ConfigureAwait(false);

                    var handler = provider.GetRequiredService(plan.HandlerType);
                    pipeline = ExecuteStreamPipeline(plan, request, handler, provider, cancellationToken);
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }

                // Enumerate manually (yield return is not allowed inside a try/catch) so a fault anywhere in the stream
                // pipeline, its disposal included, marks the activity as failed, matching the command/query path.
                Exception? failure = null;
                var enumerator = pipeline.GetAsyncEnumerator(cancellationToken);
                var disposed = false;
                abandoned = true;
                try
                {
                    while (true)
                    {
                        try
                        {
                            // Each step resumes in the consumer's flow, where neither this stream's outbox owner nor its
                            // span is current: both are made current again, so what the pipeline buffers and the spans
                            // it starts belong to this stream, and the consumer's own context is untouched.
                            if (outboxOwner is not null) OutboxOwner.Resume(outboxOwner);
                            if (activity is not null) Activity.Current = activity;
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            break;
                        }

                        yield return enumerator.Current;
                    }

                    disposed = true;
                    failure = await DisposeStreamAsync<TRequest, TItem>(enumerator, failure).ConfigureAwait(false);
                }
                finally
                {
                    // The consumer stopped enumerating early: the pipeline's stream is disposed along with this one.
                    if (!disposed)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                abandoned = false;
                if (failure is not null)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                outboxSettled = true;
                if (outboxScope is { } completedScope)
                    await completedScope.CompleteAsync().ConfigureAwait(false);

                activity?.SetStatus(ActivityStatusCode.Ok);
                succeeded = true;
            }
            finally
            {
                if (!outboxSettled) outboxScope?.Abandon();

                // A stream the consumer abandons early is a failure to the span as to the metric (and to the outbox, which
                // discards what it buffered): it did not run to completion.
                if (abandoned)
                    activity?.SetStatus(ActivityStatusCode.Error, "The stream's consumer stopped enumerating it before its end.");
                if (metered)
                    _metrics.RecordRequest(typeof(TRequest), "stream", succeeded, _timeProvider.GetElapsedTime(startedAt));
            }
        }
        finally
        {
            if (ownScope is { } scope)
                await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IAsyncEnumerable<TItem> ExecuteStreamPipeline<TRequest, TItem>(
        StreamPlan<TRequest, TItem> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        // The provider has no IStreamPipelineBehavior<TRequest, TItem> registration at all: skip the resolution.
        if (!plan.MayHaveBehaviors)
        {
            plan.MarkerCheck?.Verify(ReadOnlySpan<object>.Empty);
            return ExecuteFinalStreamAction(plan, request, handler, services, cancellationToken);
        }

        var behaviors = ResolveBehaviors<IStreamPipelineBehavior<TRequest, TItem>>(plan, services, out var behaviorCount);
        return behaviorCount == 0
            ? ExecuteFinalStreamAction(plan, request, handler, services, cancellationToken)
            : RunStreamBehaviorChain(plan, behaviors, behaviorCount, request, handler, services, cancellationToken);
    }

    // In its own method so the chain's closure is only allocated when there is a chain to run.
    private IAsyncEnumerable<TItem> RunStreamBehaviorChain<TRequest, TItem>(
        StreamPlan<TRequest, TItem> plan,
        IStreamPipelineBehavior<TRequest, TItem>[] behaviors,
        int behaviorCount,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        if (behaviorCount > 1)
            Array.Sort(behaviors, 0, behaviorCount, StreamBehaviorPriorityComparer<TRequest, TItem>.Instance);

        return InvokeBehavior(0, cancellationToken);

        IAsyncEnumerable<TItem> InvokeBehavior(int index, CancellationToken ct)
        {
            if (index >= behaviorCount)
                return ExecuteFinalStreamAction(plan, request, handler, services, ct);

            var behavior = behaviors[index];
            return behavior.Handle(request, nextToken => InvokeBehavior(index + 1, nextToken), ct);
        }
    }

    private IAsyncEnumerable<TItem> ExecuteFinalStreamAction<TRequest, TItem>(
        StreamPlan<TRequest, TItem> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        // Nothing brackets the handler and there is no outbox buffer to settle: its stream is the final action.
        if (plan.IsBareHandler && !_outboxEnabled)
            try
            {
                return plan.Invoker(handler, request, cancellationToken) ?? throw NullStream(request);
            }
            catch (Exception ex)
            {
                return Throwing<TItem>(ex);
            }

        return ExecuteBracketedStreamAsync(plan, request, handler, services, cancellationToken);
    }

    // The stream follows the command/query lifecycle contract: once StreamInitiated is published the stream ends in
    // StreamCompleted after it was enumerated to its end and every post-handler succeeded, or in StreamFailed when
    // anything failed (cancellation and timeouts included), and the outcome-aware post-handlers run on both paths. A
    // consumer that stops early, without an error, ends it with neither.
    private async IAsyncEnumerable<TItem> ExecuteBracketedStreamAsync<TRequest, TItem>(
        StreamPlan<TRequest, TItem> plan,
        TRequest request,
        object handler,
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        // Lifecycle notifications are skipped only when the provider can prove nothing subscribes to them.
        var publishesLifecycle = plan.MayHaveLifecycleSubscribers;

        if (publishesLifecycle)
            await _notifications.Publish(services, new StreamInitiatedNotification<TItem>(request), cancellationToken).ConfigureAwait(false);

        // As on the command/query path, this attempt owns what it buffers: a stream that fails before its first item
        // and is retried by the resilience behavior leaves only the successful attempt's notifications.
        var attempt = OutboxAttempt.Begin(_outboxEnabled, services);

        var yielded = 0L;
        var completed = false;
        Exception? failure = null;

        IAsyncEnumerable<TItem>? stream = null;
        try
        {
            var preHandlers = plan.PreHandlers;
            for (var i = 0; i < preHandlers.Length; i++)
                await preHandlers[i].OnBeforeHandle(request, services, cancellationToken).ConfigureAwait(false);

            stream = plan.Invoker(handler, request, cancellationToken) ?? throw NullStream(request);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Enumerated manually: yield return is not allowed inside a try/catch, and every failure of the handler's stream,
        // its disposal included, has to be captured to end the lifecycle before it is rethrown.
        if (stream is not null)
        {
            var enumerator = stream.GetAsyncEnumerator(cancellationToken);
            var disposed = false;
            try
            {
                while (true)
                {
                    try
                    {
                        // Each step starts in the consumer's flow; what the handler buffers during it is the attempt's.
                        // The last step also runs the post-handlers and the terminal publish below.
                        attempt.Resume();
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

                disposed = true;
                failure = await DisposeStreamAsync<TRequest, TItem>(enumerator, failure).ConfigureAwait(false);
                completed &= failure is null;
            }
            finally
            {
                // The consumer stopped enumerating early: the handler's stream is disposed along with this one.
                if (!disposed)
                    await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (completed)
        {
            if (plan.PostHandlers.Length > 0 &&
                await InvokePostHandlersAsync(plan.PostHandlers, request, RequestOutcome.FromResult(null), services, cancellationToken)
                    .ConfigureAwait(false) is { } postHandlerFailure)
            {
                attempt.Fail();
                if (publishesLifecycle)
                    await PublishTerminalFailureAsync<TRequest, StreamFailedNotification<TItem>>(
                        services, new StreamFailedNotification<TItem>(request, yielded, postHandlerFailure)).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(postHandlerFailure).Throw();
            }

            if (publishesLifecycle)
                try
                {
                    await _notifications.Publish(services, new StreamCompletedNotification<TItem>(request, yielded), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // StreamCompleted is already the terminal notification, so no StreamFailed follows it; but the
                    // consumer sees this failure, so the attempt's notifications go the way of any failed attempt's.
                    attempt.Fail();
                    throw;
                }
        }
        else if (failure is not null)
        {
            attempt.Fail();
            if (publishesLifecycle)
                await PublishTerminalFailureAsync<TRequest, StreamFailedNotification<TItem>>(
                    services, new StreamFailedNotification<TItem>(request, yielded, failure)).ConfigureAwait(false);
            await InvokePostHandlersAsync(plan.PostHandlers, request, RequestOutcome.FromException(failure), services, cancellationToken)
                .ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // Disposes a stream that ran to its end or failed, and returns the failure it ended with (see StreamDisposal).
    private async ValueTask<Exception?> DisposeStreamAsync<TRequest, TItem>(IAsyncEnumerator<TItem> enumerator, Exception? failure)
        where TRequest : IRequest
    {
        var (ended, suppressed) = await StreamDisposal.DisposeAsync(enumerator, failure).ConfigureAwait(false);
        if (suppressed is not null) LogStreamDisposalFailed(_logger, suppressed, typeof(TRequest).Name);
        return ended;
    }

    private static InvalidOperationException NullStream(IRequest request)
        => new($"Handler returned null stream for request '{request.GetType().Name}'.");
}

using CQRSharp.Pipelines;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

public sealed partial class PipelineExecutor
{
    private IAsyncEnumerable<TItem> ExecuteStreamInNewScope<TRequest, TItem>(
        TRequest request,
        CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync([EnumeratorCancellation] CancellationToken enumeratorToken = default)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            using var linked = CreateLinkedCancellationTokenSource(ct, enumeratorToken);
            var token = linked?.Token ?? (ct.CanBeCanceled ? ct : enumeratorToken);

            await foreach (var item in ExecuteStreamInProviderCore<TRequest, TItem>(request, scope.ServiceProvider, token)
                               .WithCancellation(token)
                               .ConfigureAwait(false))
                yield return item;
        }
    }

    private IAsyncEnumerable<TItem> ExecuteStreamInProvider<TRequest, TItem>(
        TRequest request,
        IServiceProvider provider,
        CancellationToken ct)
        where TRequest : IStreamRequest<TItem>
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync([EnumeratorCancellation] CancellationToken enumeratorToken = default)
        {
            using var linked = CreateLinkedCancellationTokenSource(ct, enumeratorToken);
            var token = linked?.Token ?? (ct.CanBeCanceled ? ct : enumeratorToken);

            await foreach (var item in ExecuteStreamInProviderCore<TRequest, TItem>(request, provider, token)
                               .WithCancellation(token)
                               .ConfigureAwait(false))
                yield return item;
        }
    }

    private async IAsyncEnumerable<TItem> ExecuteStreamInProviderCore<TRequest, TItem>(
        TRequest request,
        IServiceProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        using var activity = CqrsActivitySource.StartRequest(StreamActivityName, typeof(TRequest));

        // Same outbox ownership as the command/query path: persist leftovers when the stream completes, discard them
        // when it faults or the consumer stops enumerating early (the finally below runs on iterator disposal too).
        var outboxScope = _outboxEnabled ? RequestOutboxScope.Begin(provider) : null;
        var outboxSettled = false;
        try
        {
            IAsyncEnumerable<TItem> pipeline;
            try
            {
                // Context init is awaited (the factory may hydrate asynchronously) before the handler is resolved and
                // the stream pipeline is built, so the context is fully populated by the time the first item is produced.
                await InitializeRequestContextAsync(request, provider, cancellationToken).ConfigureAwait(false);

                var handler = GetHandler(typeof(TRequest), provider);
                pipeline = ExecuteStreamPipeline<TRequest, TItem>(request, handler, provider, cancellationToken);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            // Enumerate manually (yield return is not allowed inside a try/catch) so a fault anywhere in the stream
            // pipeline marks the activity as failed, matching the command/query path.
            Exception? failure = null;
            await using (var enumerator = pipeline.GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    yield return enumerator.Current;
                }
            }

            if (failure is not null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            outboxSettled = true;
            if (outboxScope is { } completedScope)
                await completedScope.CompleteAsync(cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            if (!outboxSettled) outboxScope?.Abandon();
        }
    }

    private IAsyncEnumerable<TItem> ExecuteStreamPipeline<TRequest, TItem>(
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        // Fresh per-resolution array (Microsoft DI); the in-place filter/sort below is concurrency-safe — see the note
        // in ExecutePipelineAsync.
        var resolved = services.GetServices<IStreamPipelineBehavior<TRequest, TItem>>();
        var behaviors = resolved as IStreamPipelineBehavior<TRequest, TItem>[] ?? resolved.ToArray();

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
            return ExecuteFinalStreamAction<TRequest, TItem>(request, handler, services, cancellationToken);

        if (behaviorCount > 1)
            Array.Sort(behaviors, 0, behaviorCount, StreamBehaviorPriorityComparer<TRequest, TItem>.Instance);

        return InvokeBehavior(0, cancellationToken);

        IAsyncEnumerable<TItem> InvokeBehavior(int index, CancellationToken ct)
        {
            if (index >= behaviorCount)
                return ExecuteFinalStreamAction<TRequest, TItem>(request, handler, services, ct);

            var behavior = behaviors[index];
            return behavior.Handle(request, nextToken => InvokeBehavior(index + 1, nextToken), ct);
        }
    }

    private IAsyncEnumerable<TItem> ExecuteFinalStreamAction<TRequest, TItem>(
        TRequest request,
        object handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TItem>
    {
        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
            var notificationDispatcher = services.GetRequiredService<INotificationDispatcher>();

            await notificationDispatcher.Publish(new StreamInitiatedNotification<TItem>(request), cancellationToken).ConfigureAwait(false);

            var yielded = 0L;
            var completed = false;
            Exception? failure = null;

            // Pre-handlers and opening the stream share the enumeration's failure path: once StreamInitiated is
            // published, a throw from either must still produce the terminal StreamFailed notification.
            IAsyncEnumerable<TItem>? stream = null;
            try
            {
                await InvokePreHandleAttributes(request, services, cancellationToken).ConfigureAwait(false);
                stream = await HandleStreamRequest<TItem>(request, handler, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Enumerate manually: yield return is not allowed inside a try/catch, so this is the only way to capture a
            // fault from the handler's stream and surface a terminal StreamFailedNotification before rethrowing it.
            if (stream is not null)
                await using (var enumerator = stream.GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        try
                        {
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
                }

            if (completed)
            {
                await InvokePostHandleAttributes(request, RequestOutcome.FromResult(null), services, cancellationToken).ConfigureAwait(false);
                await notificationDispatcher.Publish(new StreamCompletedNotification<TItem>(request, yielded), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (failure is not null)
            {
                // Plain cancellation is not a fault and the token would also block the publish; just propagate it.
                // The publish is isolated so a faulting subscriber never replaces the stream's own exception.
                if (failure is not OperationCanceledException)
                    try
                    {
                        await notificationDispatcher.Publish(new StreamFailedNotification<TItem>(request, yielded, failure), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Swallow: the stream's failure must surface, not a subscriber's.
                    }

                // Outcome-aware post-handlers observe a faulted stream too, exactly as they do a faulted command/query.
                await InvokePostHandleAttributesIsolated(request, RequestOutcome.FromException(failure), services, cancellationToken)
                    .ConfigureAwait(false);

                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            // If neither branch ran the consumer stopped enumerating early without an error; per the documented
            // contract no terminal Completed/Failed notification is published in that case.
        }
    }

    private async Task<IAsyncEnumerable<TItem>> HandleStreamRequest<TItem>(
        object request,
        object handler,
        CancellationToken cancellationToken)
    {
        if (!_handlerRegistry.TryGetHandlerDelegate(request.GetType(), out var handlerDelegate) || handlerDelegate is null)
            throw new InvalidOperationException($"No handler delegate found for request '{request.GetType().Name}'.");

        var result = await handlerDelegate(handler, request, cancellationToken).ConfigureAwait(false);
        if (result is null)
            throw new InvalidOperationException($"Handler returned null stream for request '{request.GetType().Name}'.");

        return (IAsyncEnumerable<TItem>)result;
    }

    private static CancellationTokenSource? CreateLinkedCancellationTokenSource(CancellationToken requestToken, CancellationToken enumeratorToken)
    {
        if (!requestToken.CanBeCanceled || !enumeratorToken.CanBeCanceled) return null;
        return CancellationTokenSource.CreateLinkedTokenSource(requestToken, enumeratorToken);
    }
}

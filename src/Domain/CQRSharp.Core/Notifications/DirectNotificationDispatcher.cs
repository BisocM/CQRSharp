using System.Runtime.ExceptionServices;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications.Pipelines;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Implements <see cref="IDirectNotificationDispatcher" /> to dispatch notifications directly to handlers.
///     This implementation resolves handlers from the current DI scope and awaits their execution.
/// </summary>
public class DirectNotificationDispatcher : IDirectNotificationDispatcher
{
    private const int DefaultBehaviorPriority = IPrioritizedPipelineBehavior.DefaultPriority;
    private readonly PublishStrategy _publishStrategy;

    private readonly IServiceProvider _services;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DirectNotificationDispatcher" /> class.
    /// </summary>
    /// <param name="services">The current DI scope provider.</param>
    public DirectNotificationDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _publishStrategy = services.GetService<IOptions<DispatcherOptions>>()?.Value.PublishStrategy
                           ?? PublishStrategy.ParallelWhenAllAggregate;
    }

    /// <inheritdoc />
    public virtual Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        throw new NotSupportedException(
            "Untyped notification dispatch requires the CQRSharp source generator. " +
            "Ensure you call services.AddGenerated() during startup so an AOT-safe dispatcher is registered.");
    }

    /// <inheritdoc />
    public Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var resolvedBehaviors = _services.GetServices<INotificationPipelineBehavior<TNotification>>();
        var behaviors = resolvedBehaviors as INotificationPipelineBehavior<TNotification>[] ?? resolvedBehaviors.ToArray();
        var behaviorCount = behaviors.Length;

        if (behaviorCount == 0)
            return DispatchToHandlers(notification, cancellationToken);

        if (behaviorCount > 1)
            Array.Sort(behaviors, 0, behaviorCount, BehaviorPriorityComparer<TNotification>.Instance);

        return InvokeBehavior(0, cancellationToken);

        Task InvokeBehavior(int index, CancellationToken ct)
        {
            if (index >= behaviorCount)
                return DispatchToHandlers(notification, ct);

            var behavior = behaviors[index];
            return behavior.Handle(
                notification,
                nextToken => InvokeBehavior(index + 1, nextToken),
                ct);
        }

        async Task DispatchToHandlers(TNotification n, CancellationToken ct)
        {
            var resolved = _services.GetServices<INotificationHandler<TNotification>>();
            var handlers = resolved as INotificationHandler<TNotification>[] ?? resolved.ToArray();
            if (handlers.Length == 0) return;

            // Sequential: invoke handlers one at a time, in order, stopping at the first failure.
            if (_publishStrategy == PublishStrategy.Sequential)
            {
                foreach (var handler in handlers)
                    await handler.Handle(n, ct).ConfigureAwait(false);
                return;
            }

            // Parallel strategies: start every handler, isolating a synchronous throw so it doesn't abandon siblings
            // that already started; capture it as a faulted task and surface it with the rest.
            var tasks = new List<Task>(handlers.Length);
            foreach (var handler in handlers)
                try
                {
                    tasks.Add(handler.Handle(n, ct));
                }
                catch (Exception ex)
                {
                    tasks.Add(Task.FromException(ex));
                }

            var whenAll = Task.WhenAll(tasks);

            // Parallel: the first failure surfaces (await's default; siblings are observed via the WhenAll task).
            if (_publishStrategy == PublishStrategy.Parallel)
            {
                await whenAll.ConfigureAwait(false);
                return;
            }

            // ParallelWhenAllAggregate (default): surface every failure, since await rethrows only the first.
            try
            {
                await whenAll.ConfigureAwait(false);
            }
            catch
            {
                var failures = whenAll.Exception?.InnerExceptions;
                if (failures is { Count: > 1 })
                    ExceptionDispatchInfo.Capture(new AggregateException(failures)).Throw();
                throw;
            }
        }
    }

    private sealed class BehaviorPriorityComparer<TNotification> : IComparer<INotificationPipelineBehavior<TNotification>>
        where TNotification : INotification
    {
        private BehaviorPriorityComparer()
        {
        }

        public static BehaviorPriorityComparer<TNotification> Instance { get; } = new();

        public int Compare(INotificationPipelineBehavior<TNotification>? x, INotificationPipelineBehavior<TNotification>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = x is IPrioritizedPipelineBehavior lp ? lp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var right = y is IPrioritizedPipelineBehavior rp ? rp.PipelineExecutionPriority : DefaultBehaviorPriority;
            var byPriority = left.CompareTo(right);
            if (byPriority != 0) return byPriority;

            return string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
        }
    }
}
using System.Runtime.ExceptionServices;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications.Pipelines;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Implements <see cref="IDirectNotificationDispatcher" /> to dispatch notifications directly to handlers.
///     This implementation resolves handlers from the current DI scope and awaits their execution.
/// </summary>
public class DirectNotificationDispatcher : IDirectNotificationDispatcher
{
    private const int DefaultBehaviorPriority = IPrioritizedPipelineBehavior.DefaultPriority;

    private readonly IServiceProvider _services;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DirectNotificationDispatcher" /> class.
    /// </summary>
    /// <param name="services">The current DI scope provider.</param>
    public DirectNotificationDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
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
            var handlers = _services.GetServices<INotificationHandler<TNotification>>();

            List<Task>? tasks = null;
            foreach (var handler in handlers)
            {
                tasks ??= [];

                // Isolate a synchronous throw from a handler so it doesn't abandon sibling handlers that already
                // started; capture it as a faulted task and surface it alongside the rest below.
                try
                {
                    tasks.Add(handler.Handle(n, ct));
                }
                catch (Exception ex)
                {
                    tasks.Add(Task.FromException(ex));
                }
            }

            if (tasks is null) return;

            // Await all handlers. Task.WhenAll's await rethrows only the first fault, so when more than one handler
            // fails we surface the full AggregateException instead of silently discarding the others.
            var whenAll = Task.WhenAll(tasks);
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
        public static BehaviorPriorityComparer<TNotification> Instance { get; } = new();

        private BehaviorPriorityComparer()
        {
        }

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

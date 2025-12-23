using CQRSharp.Abstractions.Data.Interfaces.Notifications;
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
    private const int DefaultBehaviorPriority = int.MaxValue / 2;

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
                tasks.Add(handler.Handle(n, ct));
            }

            if (tasks is null) return;

            await Task.WhenAll(tasks).ConfigureAwait(false);
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

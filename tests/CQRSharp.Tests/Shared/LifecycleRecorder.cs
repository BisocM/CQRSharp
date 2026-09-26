using CQRSharp.Tests.Core;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     Records, in publish order and through the real in-process publish, the lifecycle notifications of every command,
///     of every stream of <see cref="int" /> and of every query answering <see cref="LifecycleAnswer" />. Its handler is
///     private and registered by hand, so the generator never subscribes it for the rest of the assembly: an
///     assembly-wide lifecycle subscriber would switch lifecycle publishing on for every other test.
/// </summary>
public sealed class LifecycleRecorder
{
    private readonly List<INotification> _published = new();

    public IReadOnlyList<INotification> Published
    {
        get
        {
            lock (_published) return _published.ToArray();
        }
    }

    /// <summary>Registers the recorder as a subscriber of every lifecycle notification it records.</summary>
    public void Subscribe(IServiceCollection services)
    {
        var handler = new Handler(this);
        services.AddSingleton<INotificationHandler<CommandInitiatedNotification>>(handler);
        services.AddSingleton<INotificationHandler<CommandCompletedNotification>>(handler);
        services.AddSingleton<INotificationHandler<CommandFailedNotification>>(handler);
        services.AddSingleton<INotificationHandler<QueryInitiatedNotification<LifecycleAnswer>>>(handler);
        services.AddSingleton<INotificationHandler<QueryCompletedNotification<LifecycleAnswer>>>(handler);
        services.AddSingleton<INotificationHandler<QueryFailedNotification<LifecycleAnswer>>>(handler);
        services.AddSingleton<INotificationHandler<StreamInitiatedNotification<int>>>(handler);
        services.AddSingleton<INotificationHandler<StreamCompletedNotification<int>>>(handler);
        services.AddSingleton<INotificationHandler<StreamFailedNotification<int>>>(handler);
    }

    private void Record(INotification notification)
    {
        lock (_published) _published.Add(notification);
    }

    // Ignores its token: a test asserts on the lifecycle alone, and the failure path publishes under no token anyway.
    private sealed class Handler(LifecycleRecorder recorder) :
        INotificationHandler<CommandInitiatedNotification>,
        INotificationHandler<CommandCompletedNotification>,
        INotificationHandler<CommandFailedNotification>,
        INotificationHandler<QueryInitiatedNotification<LifecycleAnswer>>,
        INotificationHandler<QueryCompletedNotification<LifecycleAnswer>>,
        INotificationHandler<QueryFailedNotification<LifecycleAnswer>>,
        INotificationHandler<StreamInitiatedNotification<int>>,
        INotificationHandler<StreamCompletedNotification<int>>,
        INotificationHandler<StreamFailedNotification<int>>
    {
        public Task Handle(CommandInitiatedNotification notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(CommandFailedNotification notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(QueryInitiatedNotification<LifecycleAnswer> notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(QueryCompletedNotification<LifecycleAnswer> notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(QueryFailedNotification<LifecycleAnswer> notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(StreamInitiatedNotification<int> notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(StreamCompletedNotification<int> notification, CancellationToken cancellationToken) => Record(notification);
        public Task Handle(StreamFailedNotification<int> notification, CancellationToken cancellationToken) => Record(notification);

        private Task Record(INotification notification)
        {
            recorder.Record(notification);
            return Task.CompletedTask;
        }
    }
}

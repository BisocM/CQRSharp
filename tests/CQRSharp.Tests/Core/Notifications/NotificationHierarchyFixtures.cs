namespace CQRSharp.Tests.Core;

// Base/derived notification pairs and their handlers, shared by the notification routing, handler registration,
// subscription and registry tests.

public record BaseAuditedNotification : INotification;

public sealed record DerivedAuditedNotification : BaseAuditedNotification;

public sealed class BaseAuditRecorder
{
    public List<INotification> Seen { get; } = new();
}

public sealed class BaseAuditHandler(BaseAuditRecorder recorder) : INotificationHandler<BaseAuditedNotification>
{
    public Task Handle(BaseAuditedNotification notification, CancellationToken cancellationToken)
    {
        recorder.Seen.Add(notification);
        return Task.CompletedTask;
    }
}

public record SharedBaseEvent : INotification;

public sealed record SharedDerivedEvent : SharedBaseEvent;

public sealed class BothLevelsHandler : INotificationHandler<SharedBaseEvent>, INotificationHandler<SharedDerivedEvent>
{
    public Task Handle(SharedBaseEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Handle(SharedDerivedEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

// The fan-out hierarchy: a durable notification with a handler of its own, a handler of its base class, a handler of
// an interface it implements, and a handler declared for both it and its base class. Only the fan-out tests publish
// these, and each registers the FanOutRecorder.

public interface IFanOutAudited : INotification;

public record FanOutBaseEvent : IFanOutAudited;

[NotificationName("tests.fanout.order-placed")]
public sealed record FanOutOrderPlaced : FanOutBaseEvent;

public sealed class FanOutRecorder
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _deliveries = new();

    public void Add(string delivery) => _deliveries.Enqueue(delivery);

    public string[] Deliveries => _deliveries.ToArray();
}

[NotificationHandlerName("tests.fanout.own")]
public sealed class FanOutOwnHandler(FanOutRecorder recorder) : INotificationHandler<FanOutOrderPlaced>
{
    public Task Handle(FanOutOrderPlaced notification, CancellationToken cancellationToken)
    {
        recorder.Add("tests.fanout.own");
        return Task.CompletedTask;
    }
}

[NotificationHandlerName("tests.fanout.base")]
public sealed class FanOutBaseHandler(FanOutRecorder recorder) : INotificationHandler<FanOutBaseEvent>
{
    public Task Handle(FanOutBaseEvent notification, CancellationToken cancellationToken)
    {
        recorder.Add("tests.fanout.base");
        return Task.CompletedTask;
    }
}

[NotificationHandlerName("tests.fanout.auditor")]
public sealed class FanOutAuditor(FanOutRecorder recorder) : INotificationHandler<IFanOutAudited>
{
    public Task Handle(IFanOutAudited notification, CancellationToken cancellationToken)
    {
        recorder.Add("tests.fanout.auditor");
        return Task.CompletedTask;
    }
}

[NotificationHandlerName("tests.fanout.both")]
public sealed class FanOutBothLevelsHandler(FanOutRecorder recorder)
    : INotificationHandler<FanOutBaseEvent>, INotificationHandler<FanOutOrderPlaced>
{
    public Task Handle(FanOutBaseEvent notification, CancellationToken cancellationToken)
    {
        recorder.Add("tests.fanout.both:base");
        return Task.CompletedTask;
    }

    public Task Handle(FanOutOrderPlaced notification, CancellationToken cancellationToken)
    {
        recorder.Add("tests.fanout.both:derived");
        return Task.CompletedTask;
    }
}

// A handler declared for two interfaces a notification implements, one of them generic: they tie on nearness, so the
// name decides, and the in-process publish and the outbox must decide alike.

public interface ITieEvent<T> : INotification;

public interface ITieEventA : INotification;

public sealed record TieNotification : ITieEvent<int>, ITieEventA;

public sealed class TieRecorder
{
    public List<string> Seen { get; } = new();
}

public sealed class TieHandler(TieRecorder recorder) : INotificationHandler<ITieEvent<int>>, INotificationHandler<ITieEventA>
{
    public Task Handle(ITieEvent<int> notification, CancellationToken cancellationToken)
    {
        recorder.Seen.Add("generic");
        return Task.CompletedTask;
    }

    public Task Handle(ITieEventA notification, CancellationToken cancellationToken)
    {
        recorder.Seen.Add("plain");
        return Task.CompletedTask;
    }
}

using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="OutboxMessageFactory" />: the creation time of the messages it builds is their place in the delivery
///     order, strictly increasing through a batch, except for a scheduled notification still ahead of its due time, whose
///     messages take that time as their place and their not-before time.
/// </summary>
public sealed class OutboxMessageFactoryTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly SingleTypeNotificationSerializer<TestNotification> _serializer = new("test.notification");
    private readonly FakeSubscriptionRegistry _subscriptions = new(NotificationSubscription.For<Handler, TestNotification>("Tests.Handler"));

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Fact(DisplayName = "Messages of one batch get strictly increasing creation times, and none is scheduled")]
    public void A_batch_is_stamped_in_publication_order()
    {
        var messages = OutboxMessageFactory.Create(
            [new OutboxEntry(new TestNotification()), new OutboxEntry(new TestNotification())], _serializer, _subscriptions, transports: null, _time);

        messages.Select(m => m.CreatedAt).Should().Equal(Now, Now.AddTicks(1));
        messages.Should().OnlyContain(m => m.NextRetryAt == null);
    }

    [Fact(DisplayName = "A scheduled notification takes its due time as its creation and not-before time, and leaves the stamps around it alone")]
    public void A_scheduled_notification_is_stamped_with_its_due_time()
    {
        var dueAt = Now.AddHours(2);

        var messages = OutboxMessageFactory.Create(
            [
                new OutboxEntry(new TestNotification()),
                new OutboxEntry(new TestNotification(), dueAt),
                new OutboxEntry(new TestNotification())
            ],
            _serializer, _subscriptions, transports: null, _time);

        messages.Select(m => (m.CreatedAt, m.NextRetryAt)).Should().Equal(
            (Now, (DateTime?)null),
            (dueAt, dueAt),
            (Now.AddTicks(1), (DateTime?)null));
    }

    [Theory(DisplayName = "A scheduled notification that is due already is stamped as if it were published now")]
    [InlineData(0)]
    [InlineData(-60)]
    public void A_notification_already_due_is_stamped_now(int minutesFromNow)
    {
        var messages = OutboxMessageFactory.Create(
            [new OutboxEntry(new TestNotification()), new OutboxEntry(new TestNotification(), Now.AddMinutes(minutesFromNow))],
            _serializer, _subscriptions, transports: null, _time);

        messages.Select(m => (m.CreatedAt, m.NextRetryAt)).Should().Equal(
            (Now, (DateTime?)null),
            (Now.AddTicks(1), (DateTime?)null));
    }

    private sealed class Handler : INotificationHandler<TestNotification>
    {
        public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

using System.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Core.Transports;
using CQRSharp.Persistence;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="OutboxMessageFactory" /> with notification transports: a transport that routes a notification is one more
///     recipient, with a message of its own after the local handlers' messages and the same stamps, and a notification
///     received through a transport is stored for the local handlers alone.
/// </summary>
public sealed class OutboxMessageFactoryTransportTests : IAsyncDisposable
{
    private const string Forwarded = "tests.transport.forwarded";
    private const string Shared = "tests.transport.shared";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider? _provider;

    public ValueTask DisposeAsync() => _provider?.DisposeAsync() ?? ValueTask.CompletedTask;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private (INotificationSerializer Serializer, INotificationSubscriptionRegistry Subscriptions, NotificationTransportRegistry Transports) Build(
        params INotificationTransport[] transports)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<SharedNotificationLog>();
        foreach (var transport in transports)
            services.AddSingleton(transport);
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore()));
        _provider = services.BuildServiceProvider();
        return (
            _provider.GetRequiredService<INotificationSerializer>(),
            _provider.GetRequiredService<INotificationSubscriptionRegistry>(),
            _provider.GetRequiredService<NotificationTransportRegistry>());
    }

    [Fact(DisplayName = "Transports: a notification no handler receives is stored once, for the transport that routes it")]
    public void A_notification_only_a_transport_takes_is_stored_for_it()
    {
        var (serializer, subscriptions, transports) = Build(new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded));

        var messages = OutboxMessageFactory.Create([new OutboxEntry(new ForwardedNotification(1, "k"))], serializer, subscriptions, transports, _time);

        var message = messages.Should().ContainSingle().Subject;
        message.HandlerName.Should().Be(ScriptedTransport.DefaultName);
        message.NotificationType.Should().Be(Forwarded);
        message.PartitionKey.Should().Be("k", "the notification's own partition key orders the transport's messages too");
        message.NotificationId.Should().NotBeNull();
        message.Status.Should().Be(OutboxMessageStatus.Pending);
        serializer.Deserialize(Forwarded, message.Payload).Should().Be(new ForwardedNotification(1, "k"));
    }

    [Fact(DisplayName = "Transports: the handlers' messages come first, then one per routing transport, all with the same stamps")]
    public void Handler_and_transport_messages_share_their_stamps()
    {
        var (serializer, subscriptions, transports) = Build(new ScriptedTransport("first", Shared), new ScriptedTransport("second", Shared));
        var handler = subscriptions.GetSubscriptions(typeof(SharedNotification)).Should().ContainSingle().Subject.HandlerName;

        using var publishing = new Activity("publishing").Start();
        var messages = OutboxMessageFactory.Create([new OutboxEntry(new SharedNotification(7))], serializer, subscriptions, transports, _time);

        messages.Select(m => m.HandlerName).Should().Equal(handler, "first", "second");
        messages.Select(m => m.NotificationId).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        messages.Should().OnlyContain(m => m.CreatedAt == Now && m.NextRetryAt == null && m.NotificationType == Shared);
        messages.Should().OnlyContain(m => m.TraceParent == publishing.Id);
        messages.Select(m => m.Payload).Distinct().Should().ContainSingle("the notification is serialized once for every recipient");
        messages.Select(m => m.Id).Distinct().Should().HaveCount(3);
    }

    [Fact(DisplayName = "Transports: a scheduled notification is forwarded at its due time, like its handlers' messages")]
    public void A_scheduled_notification_is_forwarded_when_due()
    {
        var (serializer, subscriptions, transports) = Build(new ScriptedTransport(ScriptedTransport.DefaultName, Shared, Forwarded));
        var dueAt = Now.AddMinutes(30);

        var messages = OutboxMessageFactory.Create(
            [new OutboxEntry(new SharedNotification(1), dueAt), new OutboxEntry(new ForwardedNotification(2, null), dueAt)],
            serializer, subscriptions, transports, _time);

        messages.Should().HaveCount(3);
        messages.Should().OnlyContain(m => m.CreatedAt == dueAt && m.NextRetryAt == dueAt);
        messages.Count(m => m.HandlerName == ScriptedTransport.DefaultName).Should().Be(2);
    }

    [Fact(DisplayName = "Transports: a notification received through a transport is stored for the local handlers alone")]
    public void A_received_notification_is_never_forwarded()
    {
        var (serializer, subscriptions, _) = Build(new ScriptedTransport(ScriptedTransport.DefaultName, Shared, Forwarded));

        var messages = OutboxMessageFactory.Create(
            [new OutboxEntry(new SharedNotification(1)), new OutboxEntry(new ForwardedNotification(2, null))],
            serializer, subscriptions, transports: null, _time);

        messages.Should().ContainSingle().Which.HandlerName.Should().NotBe(ScriptedTransport.DefaultName);
    }

    [Fact(DisplayName = "Transports: a notification no transport routes is stored for its handlers only, and one nobody takes not at all")]
    public void An_unrouted_notification_gets_no_transport_message()
    {
        var (serializer, subscriptions, transports) = Build(new ScriptedTransport(ScriptedTransport.DefaultName));

        var messages = OutboxMessageFactory.Create(
            [new OutboxEntry(new SharedNotification(1)), new OutboxEntry(new ForwardedNotification(2, null))],
            serializer, subscriptions, transports, _time);

        messages.Should().ContainSingle().Which.NotificationType.Should().Be(Shared);
    }

    [Fact(DisplayName = "Transports: each transport is asked once per notification type, and the answer is kept")]
    public void Routing_is_asked_once_per_type()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded);
        var (serializer, subscriptions, transports) = Build(transport);

        for (var i = 0; i < 3; i++)
            OutboxMessageFactory.Create(
                [new OutboxEntry(new ForwardedNotification(i, null)), new OutboxEntry(new SharedNotification(i))],
                serializer, subscriptions, transports, _time);

        transport.RouteQuestions.Should().BeEquivalentTo([(Forwarded, typeof(ForwardedNotification)), (Shared, typeof(SharedNotification))]);
    }
}

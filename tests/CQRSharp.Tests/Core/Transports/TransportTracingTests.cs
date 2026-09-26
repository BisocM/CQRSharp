using System.Collections.Concurrent;
using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Persistence;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The trace across a transport: a send carries the processor's dispatch span as its <c>traceparent</c>, a child of the
///     span that published the notification, and the publishing span itself when no dispatch span is recorded, so the trace
///     stays connected with tracing off in the processor. On the receiving side the intake's span continues the sender's
///     trace. In the tracing collection: a listener changes the dispatch path of every test running beside it.
/// </summary>
[Collection(TracingCollection.Name)]
public sealed class TransportTracingTests
{
    private const string Forwarded = "tests.transport.forwarded";

    [Fact(DisplayName = "Transport tracing: a send propagates the dispatch span, a child of the publishing span")]
    public async Task A_send_carries_the_dispatch_span()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded);
        var (provider, _, _) = BuildProbed(configureServices: s => s.AddSingleton<INotificationTransport>(transport));
        await using var _ = provider;

        using (new Activity("publishing").Start())
            await PublishAsync(provider, new ForwardedNotification(1, "k"));
        var stored = Stored(provider).Should().ContainSingle().Subject;

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        await DrainAsync(provider);
        await processor.StopAsync(CancellationToken.None);

        var dispatch = transport.SendActivities.Should().ContainSingle().Subject!;
        dispatch.OperationName.Should().Be("CQRS Outbox Dispatch");
        dispatch.Kind.Should().Be(ActivityKind.Consumer);
        dispatch.ParentId.Should().Be(stored.TraceParent, "the dispatch span continues the publishing trace");
        dispatch.GetTagItem(CqrsTelemetry.Tags.NotificationHandler).Should().Be(ScriptedTransport.DefaultName);
        transport.Sends.Should().ContainSingle().Which.TraceParent.Should().Be(dispatch.Id);
    }

    [Fact(DisplayName = "Transport tracing: without a dispatch span, a send propagates the publishing span")]
    public async Task Without_a_dispatch_span_the_publishing_span_is_propagated()
    {
        var transport = new ScriptedTransport(ScriptedTransport.DefaultName, Forwarded);
        var (provider, _, _) = BuildProbed(configureServices: s => s.AddSingleton<INotificationTransport>(transport));
        await using var _ = provider;

        using (new Activity("publishing").Start())
            await PublishAsync(provider, new ForwardedNotification(1, null));
        var stored = Stored(provider).Should().ContainSingle().Subject;
        stored.TraceParent.Should().NotBeNull();

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        await DrainAsync(provider);
        await processor.StopAsync(CancellationToken.None);

        transport.Sends.Should().ContainSingle().Which.TraceParent.Should().Be(stored.TraceParent);
    }

    [Fact(DisplayName = "Transport tracing: the intake's CQRS Transport Receive span continues the sender's trace and is tagged")]
    public async Task The_intake_span_continues_the_senders_trace()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var (provider, _, _) = BuildProbed(configureServices: s => s.AddSingleton<SharedNotificationLog>());
        await using var _ = provider;
        var payload = provider.GetRequiredService<INotificationSerializer>().Serialize(new SharedNotification(1));
        const string senderTrace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INotificationIntake>().AcceptAsync(
                new InboundNotification("broker", "broker:queue", "message-1", "tests.transport.shared", payload, null, senderTrace, "vendor=value"),
                CancellationToken.None);

        var receive = spans.Should().ContainSingle(a => a.OperationName == "CQRS Transport Receive").Subject;
        receive.Kind.Should().Be(ActivityKind.Consumer);
        receive.ParentId.Should().Be(senderTrace);
        receive.TraceStateString.Should().Be("vendor=value");
        receive.GetTagItem(CqrsTelemetry.Tags.Transport).Should().Be("broker");
        receive.GetTagItem(CqrsTelemetry.Tags.NotificationName).Should().Be("tests.transport.shared");
        receive.GetTagItem("messaging.message.id").Should().Be("message-1");
        receive.Status.Should().Be(ActivityStatusCode.Ok);
        Stored(provider).Should().ContainSingle().Which.TraceParent.Should().Be(receive.Id, "the local deliveries continue the received trace");
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CQRSharp.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The spans CQRSharp emits on its <see cref="CqrsTelemetry.ActivitySourceName" /> source, observed through real
///     dispatch: nothing while nobody listens; one span per request, named for its kind and type, parented to the caller's
///     span and never left behind as the caller's <see cref="Activity.Current" />; its status set by the outcome; a queued
///     request under the span of its queueing; and an outbox delivery under the span that published the message.
/// </summary>
/// <remarks>
///     An <see cref="ActivityListener" /> subscribes process-wide, and a subscribed source moves every dispatch in the
///     process off the untraced fast path. Every test that subscribes one therefore runs in the
///     <see cref="TracingCollection" />, which runs on its own: other tests keep the path they are written for, and the
///     no-listener test sees none. A listener still sees every span started anywhere while it is subscribed, so each test
///     reads only its own trace.
/// </remarks>
[Collection(TracingCollection.Name)]
public class CqrsActivitySourceTests
{
    [Fact(DisplayName = "A traced request's span is named for its kind and type, tagged with its type, and parented to the caller's span")]
    public async Task Request_spans_are_named_for_their_kind_and_type()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        using var caller = new Activity("caller").Start();
        await dispatcher.Send(new TracedCommand(), TestContext.Current.CancellationToken);
        await dispatcher.Send(new TracedValueCommand(), TestContext.Current.CancellationToken);
        await dispatcher.Send(new TracedQuery(), TestContext.Current.CancellationToken);
        await foreach (var _ in dispatcher.Stream(new TracedStream(), TestContext.Current.CancellationToken))
        {
        }

        var spans = stopped.Where(a => a.TraceId == caller.TraceId).ToList();
        spans.Select(a => $"{a.OperationName} [{a.GetTagItem(CqrsTelemetry.Tags.RequestType)}]").Should().BeEquivalentTo([
            $"CQRS Command {nameof(TracedCommand)} [{typeof(TracedCommand)}]",
            $"CQRS Command {nameof(TracedValueCommand)} [{typeof(TracedValueCommand)}]",
            $"CQRS Query {nameof(TracedQuery)} [{typeof(TracedQuery)}]",
            $"CQRS Stream {nameof(TracedStream)} [{typeof(TracedStream)}]"
        ]);
        spans.Should().OnlyContain(a => a.ParentSpanId == caller.SpanId);
    }

    [Fact(DisplayName = "Nothing is traced while nothing listens: the caller's span is what the handler sees")]
    public async Task Nothing_is_traced_without_a_listener()
    {
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var query = new TracedQuery();
        var stream = new TracedStream();

        using var caller = new Activity("caller").Start();
        await dispatcher.Send(query, TestContext.Current.CancellationToken);
        await foreach (var _ in dispatcher.Stream(stream, TestContext.Current.CancellationToken))
        {
        }

        query.CurrentInHandler.Should().BeSameAs(caller);
        stream.CurrentPerItem.Should().HaveCount(3).And.OnlyContain(current => ReferenceEquals(current, caller));
    }

    [Theory(DisplayName = "A traced Send leaves no span behind as the caller's Activity.Current")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_restores_the_callers_current_activity_when_there_is_none(bool handlerYields)
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        Activity.Current = null;

        await dispatcher.Send(new TracedQuery { Yield = handlerYields }, TestContext.Current.CancellationToken);

        Activity.Current.Should().BeNull("the request's span belongs to the request, not to its caller");
        stopped.Should().Contain(a => a.OperationName == "CQRS Query " + nameof(TracedQuery), "the request was traced");
    }

    [Theory(DisplayName = "Consecutive traced Sends are siblings under the caller's span, which stays current")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consecutive_sends_are_siblings_under_the_caller(bool handlerYields)
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        using var caller = new Activity("caller").Start();
        await dispatcher.Send(new TracedQuery { Yield = handlerYields }, TestContext.Current.CancellationToken);
        Activity.Current.Should().BeSameAs(caller);
        await dispatcher.Send(new TracedQuery { Yield = handlerYields }, TestContext.Current.CancellationToken);
        Activity.Current.Should().BeSameAs(caller);

        var spans = stopped.Where(a => a.TraceId == caller.TraceId).ToList();
        spans.Should().HaveCount(2).And.OnlyContain(a => a.ParentSpanId == caller.SpanId);
    }

    [Fact(DisplayName = "Requests a handler sends are siblings under that handler's request span")]
    public async Task Nested_sends_are_siblings_under_their_request()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        using var caller = new Activity("caller").Start();
        await dispatcher.Send(new TracedParentQuery(), TestContext.Current.CancellationToken);
        Activity.Current.Should().BeSameAs(caller);

        var spans = stopped.Where(a => a.TraceId == caller.TraceId).ToList();
        var parent = spans.Should().ContainSingle(a => a.OperationName == "CQRS Query " + nameof(TracedParentQuery)).Subject;
        parent.ParentSpanId.Should().Be(caller.SpanId);
        spans.Where(a => a.OperationName == "CQRS Query " + nameof(TracedQuery)).Should()
            .HaveCount(2).And.OnlyContain(a => a.ParentSpanId == parent.SpanId);
    }

    [Fact(DisplayName = "Every step of a traced stream runs under the stream's span, and the consumer's span is current between items")]
    public async Task Stream_span_is_current_for_every_item()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var request = new TracedStream();

        using var consumer = new Activity("consumer").Start();
        await foreach (var _ in dispatcher.Stream(request, TestContext.Current.CancellationToken))
            Activity.Current.Should().BeSameAs(consumer, "the stream's span belongs to the stream's steps, not to its consumer");

        var span = stopped.Should().ContainSingle(a => a.TraceId == consumer.TraceId && a.OperationName == "CQRS Stream " + nameof(TracedStream)).Subject;
        request.CurrentPerItem.Should().HaveCount(3).And.OnlyContain(current => ReferenceEquals(current, span));
    }

    [Fact(DisplayName = "Every step of a traced stream runs under the innermost span a stream behavior holds open")]
    public async Task Behavior_span_is_current_for_every_item()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped, CqrsTelemetry.ActivitySourceNames);
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseTimeout(o => o.Timeout = TimeSpan.FromMinutes(5)));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var request = new TracedStream();

        using var consumer = new Activity("consumer").Start();
        await foreach (var _ in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(request, TestContext.Current.CancellationToken))
            Activity.Current.Should().BeSameAs(consumer);

        var spans = stopped.Where(a => a.TraceId == consumer.TraceId).ToList();
        var stream = spans.Should().ContainSingle(a => a.OperationName == "CQRS Stream " + nameof(TracedStream)).Subject;
        var guard = spans.Should().ContainSingle(a => a.OperationName == "Timeout.Guard").Subject;
        guard.ParentSpanId.Should().Be(stream.SpanId);
        request.CurrentPerItem.Should().HaveCount(3).And.OnlyContain(current => ReferenceEquals(current, guard));
    }

    [Fact(DisplayName = "A command that returns a failed result ends its span with status Error, as the metric records a failure")]
    public async Task Failed_result_marks_the_span_as_an_error()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        using var caller = new Activity("caller").Start();
        (await dispatcher.Send(new TracedCommand { Decline = true }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeFalse();
        (await dispatcher.Send(new TracedCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        var spans = stopped.Where(a => a.TraceId == caller.TraceId).ToList();
        spans.Should().HaveCount(2);
        spans.Should().ContainSingle(a => a.Status == ActivityStatusCode.Error && a.StatusDescription == "declined");
        spans.Should().ContainSingle(a => a.Status == ActivityStatusCode.Ok);
    }

    [Fact(DisplayName = "A stream its consumer stops early ends its span with status Error, as the metric records a failure")]
    public async Task Abandoned_stream_marks_the_span_as_an_error()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        using var consumer = new Activity("consumer").Start();
        await foreach (var _ in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new TracedStream(), TestContext.Current.CancellationToken))
            break;

        stopped.Should().ContainSingle(a => a.TraceId == consumer.TraceId && a.OperationName == "CQRS Stream " + nameof(TracedStream))
            .Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact(DisplayName = "A request or stream that throws ends its span with status Error and the exception's message; one that succeeds, Ok")]
    public async Task Thrown_failures_mark_the_span_as_an_error()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        await using var provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        using var caller = new Activity("caller").Start();
        var query = () => dispatcher.Send(new TracedQuery { Throw = true });
        await query.Should().ThrowAsync<InvalidOperationException>();
        var faulting = async () =>
        {
            await foreach (var _ in dispatcher.Stream(new TracedFaultingStream()))
            {
            }
        };
        await faulting.Should().ThrowAsync<InvalidOperationException>();
        await foreach (var _ in dispatcher.Stream(new TracedStream(), TestContext.Current.CancellationToken))
        {
        }

        var spans = stopped.Where(a => a.TraceId == caller.TraceId).ToList();
        spans.Should().ContainSingle(a => a.OperationName == "CQRS Query " + nameof(TracedQuery))
            .Which.Should().Match<Activity>(a => a.Status == ActivityStatusCode.Error && a.StatusDescription == "traced query failed");
        spans.Should().ContainSingle(a => a.OperationName == "CQRS Stream " + nameof(TracedFaultingStream))
            .Which.Should().Match<Activity>(a => a.Status == ActivityStatusCode.Error && a.StatusDescription == "traced stream failed");
        spans.Should().ContainSingle(a => a.OperationName == "CQRS Stream " + nameof(TracedStream))
            .Which.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact(DisplayName = "A queued request is traced under a queueing span parented to the caller's span")]
    public async Task Queued_request_is_traced_under_the_caller()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddCqrsGenerated(b => b.ConfigureDispatcher(o => o.RunMode = RunMode.Queued)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        ActivityTraceId trace;
        ActivitySpanId callerSpan;
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            using var caller = new Activity("caller").Start();
            (trace, callerSpan) = (caller.TraceId, caller.SpanId);

            await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new TracedQuery(), TestContext.Current.CancellationToken);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        var spans = stopped.Where(a => a.TraceId == trace).ToList();
        var queued = spans.Should().ContainSingle(a => a.OperationName == "CQRS Queued " + nameof(TracedQuery)).Subject;
        queued.ParentSpanId.Should().Be(callerSpan);
        queued.GetTagItem(CqrsTelemetry.Tags.RequestType).Should().Be(typeof(TracedQuery).ToString());
        spans.Should().ContainSingle(a => a.OperationName == "CQRS Query " + nameof(TracedQuery))
            .Which.ParentSpanId.Should().Be(queued.SpanId);
    }

    [Fact(DisplayName = "A notification a traced command publishes to the outbox carries the command's span, and its delivery is traced under it")]
    public async Task Outbox_delivery_is_traced_under_the_publishing_request()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        var (provider, _, _) = OutboxTestHarness.BuildProbed();
        await using var disposeProvider = provider;

        ActivityTraceId trace;
        using (var caller = new Activity("caller").Start())
        {
            trace = caller.TraceId;
            await using var scope = provider.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new TracedPublishingCommand(), TestContext.Current.CancellationToken))
                .IsSuccess.Should().BeTrue();
        }

        var request = stopped.Should().ContainSingle(a => a.TraceId == trace && a.OperationName == "CQRS Command " + nameof(TracedPublishingCommand)).Subject;
        OutboxTestHarness.Stored(provider).Should().ContainSingle().Which.TraceParent.Should().Be(request.Id);

        var delivery = await DeliverAndTraceAsync(provider, stopped, trace);
        delivery.ParentSpanId.Should().Be(request.SpanId);
    }

    [Fact(DisplayName = "Delivering an outbox message is a Consumer span under the span that published it, tagged with the message's names")]
    public async Task Outbox_delivery_is_a_consumer_span_of_its_publisher()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(stopped);
        var (provider, _, _) = OutboxTestHarness.BuildProbed();
        await using var disposeProvider = provider;

        ActivityTraceId trace;
        ActivitySpanId publisherSpan;
        string? publisherId;
        using (var publisher = new Activity("publisher").Start())
        {
            (trace, publisherSpan, publisherId) = (publisher.TraceId, publisher.SpanId, publisher.Id);
            await OutboxTestHarness.PublishAsync(provider, new ParallelProbe(1, "partition-a"));
        }

        var message = OutboxTestHarness.Stored(provider).Should().ContainSingle().Subject;
        message.TraceParent.Should().Be(publisherId);

        var span = await DeliverAndTraceAsync(provider, stopped, trace);
        span.Kind.Should().Be(ActivityKind.Consumer);
        span.ParentSpanId.Should().Be(publisherSpan);
        span.GetTagItem(CqrsTelemetry.Tags.NotificationName).Should().Be("tests.parallel.probe");
        span.GetTagItem(CqrsTelemetry.Tags.NotificationHandler).Should().Be(message.HandlerName);
        span.GetTagItem(CqrsTelemetry.Tags.PartitionKey).Should().Be("partition-a");
    }

    // Runs the provider's processor until it has delivered the stored message, and returns that delivery's span.
    private static async Task<Activity> DeliverAndTraceAsync(IServiceProvider provider, ConcurrentBag<Activity> stopped, ActivityTraceId trace)
    {
        var processor = OutboxTestHarness.Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await OutboxTestHarness.DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        return stopped.Single(a => a.TraceId == trace && a.OperationName == "CQRS Outbox Dispatch");
    }

    private static ActivityListener Listen(ConcurrentBag<Activity> sink, params string[] sources)
    {
        if (sources.Length == 0) sources = [CqrsTelemetry.ActivitySourceName];

        // Process-wide: it sees every span started while it is subscribed, so every assertion filters what it reads.
        var listener = new ActivityListener
        {
            ShouldListenTo = source => sources.Contains(source.Name),
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = sink.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>The tests whose ActivityListener would otherwise change the dispatch path of every test running beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TracingCollection
{
    public const string Name = "Tracing";
}

public sealed class TracedQuery : QueryBase<int>
{
    public bool Yield { get; init; }
    public bool Throw { get; init; }

    /// <summary>What was <see cref="Activity.Current" /> when the handler ran.</summary>
    public Activity? CurrentInHandler { get; set; }
}

public sealed class TracedQueryHandler : IQueryHandler<TracedQuery, int>
{
    public async Task<int> Handle(TracedQuery query, CancellationToken cancellationToken)
    {
        query.CurrentInHandler = Activity.Current;
        if (query.Yield) await Task.Yield();
        return query.Throw ? throw new InvalidOperationException("traced query failed") : 1;
    }
}

public sealed class TracedParentQuery : QueryBase<int>;

public sealed class TracedParentQueryHandler(ICqrsDispatcher dispatcher) : IQueryHandler<TracedParentQuery, int>
{
    public async Task<int> Handle(TracedParentQuery query, CancellationToken cancellationToken)
        => await dispatcher.Send(new TracedQuery(), cancellationToken) + await dispatcher.Send(new TracedQuery { Yield = true }, cancellationToken);
}

public sealed class TracedStream : StreamRequestBase<int>
{
    public List<Activity?> CurrentPerItem { get; } = [];
}

public sealed class TracedStreamHandler : IStreamRequestHandler<TracedStream, int>
{
    public async IAsyncEnumerable<int> Handle(TracedStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            // Resumed by the consumer's MoveNextAsync: what is current here is what a span started per item would parent to.
            await Task.Yield();
            request.CurrentPerItem.Add(Activity.Current);
            yield return i;
        }
    }
}

public sealed class TracedCommand : CommandBase
{
    public bool Decline { get; init; }
}

public sealed class TracedCommandHandler : ICommandHandler<TracedCommand>
{
    public Task<CommandResult> Handle(TracedCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Decline ? CommandResult.FromError("declined") : CommandResult.FromSuccess());
}

public sealed class TracedValueCommand : ResultCommandBase<int>;

public sealed class TracedValueCommandHandler : IResultCommandHandler<TracedValueCommand, int>
{
    public Task<CommandResult<int>> Handle(TracedValueCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult<int>.FromSuccess(1));
}

public sealed class TracedFaultingStream : StreamRequestBase<int>;

public sealed class TracedFaultingStreamHandler : IStreamRequestHandler<TracedFaultingStream, int>
{
    public async IAsyncEnumerable<int> Handle(TracedFaultingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("traced stream failed");
    }
}

public sealed class TracedPublishingCommand : CommandBase;

public sealed class TracedPublishingCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<TracedPublishingCommand>
{
    public async Task<CommandResult> Handle(TracedPublishingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new ParallelProbe(1, "traced"), cancellationToken);
        return CommandResult.FromSuccess();
    }
}

using System.Runtime.CompilerServices;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Tests for <see cref="LoggingBehavior{TRequest,TResult}" /> and
///     <see cref="StreamLoggingBehavior{TRequest, TItem}" />: the behaviors are transparent (they invoke the next
///     delegate and return its result/items unchanged) and they log around the request — on the success path, when the
///     next delegate throws, and when the caller cancels. Elapsed times are read from a <see cref="FakeTimeProvider" />
///     the tests move by hand, so the logged durations are exact.
/// </summary>
public class LoggingBehaviorTests
{
    private static readonly TimeSpan HandlerTime = TimeSpan.FromMilliseconds(42);

    private readonly FakeTimeProvider _time = new();

    private (LoggingBehavior<TestCommand, string> Behavior, CapturingLogger<LoggingBehavior<TestCommand, string>> Logger) CreateLoggingBehavior()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, string>>();
        return (new LoggingBehavior<TestCommand, string>(logger, _time), logger);
    }

    private (StreamLoggingBehavior<TestStreamRequest, int> Behavior, CapturingLogger<StreamLoggingBehavior<TestStreamRequest, int>> Logger) CreateStreamLoggingBehavior()
    {
        var logger = new CapturingLogger<StreamLoggingBehavior<TestStreamRequest, int>>();
        return (new StreamLoggingBehavior<TestStreamRequest, int>(logger, _time), logger);
    }

    // -------------------------------------------------------------------------------------------------------------
    // LoggingBehavior<TRequest, TResult>
    // -------------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Logging behavior invokes the next delegate and returns its result unchanged")]
    public async Task Logging_InvokesNext_AndReturnsResultUnchanged()
    {
        var (behavior, _) = CreateLoggingBehavior();
        var expected = "the-result";
        var called = 0;

        var result = await behavior.Handle(
            new TestCommand(),
            _ =>
            {
                called++;
                return Task.FromResult(expected);
            },
            CancellationToken.None);

        called.Should().Be(1, "the behavior must delegate to the inner handler exactly once");
        result.Should().BeSameAs(expected, "the result must be returned unchanged");
    }

    [Fact(DisplayName = "Logging behavior logs before and after a successful request, with the elapsed time")]
    public async Task Logging_OnSuccess_LogsAroundRequest()
    {
        var (behavior, logger) = CreateLoggingBehavior();

        await behavior.Handle(
            new TestCommand(),
            _ =>
            {
                _time.Advance(HandlerTime);
                return Task.FromResult("ok");
            },
            CancellationToken.None);

        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal(
            (4000, LogLevel.Information), (4001, LogLevel.Information));
        logger.Entries[0].Message.Should().Be($"Handling {nameof(TestCommand)}");
        logger.Entries[1].Message.Should().Be($"Handled {nameof(TestCommand)} in 42ms");
        logger.Entries.Should().NotContain(e => e.Exception != null);
    }

    [Fact(DisplayName = "Logging behavior propagates the next delegate's exception and logs it once at Error, with the elapsed time")]
    public async Task Logging_OnFailure_LogsError_AndPropagates()
    {
        var (behavior, logger) = CreateLoggingBehavior();
        var boom = new InvalidOperationException("boom");

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            _ =>
            {
                _time.Advance(HandlerTime);
                throw boom;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // The start log, then the failure (no completion log).
        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal(
            (4000, LogLevel.Information), (4002, LogLevel.Error));
        logger.Entries[1].Message.Should().Be($"Request {nameof(TestCommand)} failed after 42ms");
        logger.Entries[1].Exception.Should().BeSameAs(boom);
    }

    [Fact(DisplayName = "Logging behavior logs caller cancellation at Information, not as an error")]
    public async Task Logging_CallerCancellation_IsNotAnError()
    {
        var (behavior, logger) = CreateLoggingBehavior();
        using var caller = new CancellationTokenSource();

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            ct =>
            {
                caller.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("never");
            },
            caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4006 && e.Level == LogLevel.Information);
    }

    [Fact(DisplayName = "Logging behavior logs a cancellation the caller did not request (a dependency's) as a failure")]
    public async Task Logging_NonCallerCancellation_IsAnError()
    {
        var (behavior, logger) = CreateLoggingBehavior();

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            _ => throw new TaskCanceledException("simulated HttpClient timeout"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4002 && e.Level == LogLevel.Error);
    }

    [Theory(DisplayName = "Logging behavior logs an outcome the pipeline produces on purpose in one line below Error, without the stack trace")]
    [InlineData(PipelineOutcome.Invalid, 4008, LogLevel.Information)]
    [InlineData(PipelineOutcome.Duplicate, 4008, LogLevel.Information)]
    [InlineData(PipelineOutcome.KeyReused, 4008, LogLevel.Information)]
    [InlineData(PipelineOutcome.RateLimited, 4008, LogLevel.Information)]
    [InlineData(PipelineOutcome.TimedOut, 4009, LogLevel.Warning)]
    [InlineData(PipelineOutcome.QueueRefused, 4009, LogLevel.Warning)]
    public async Task Logging_PipelineOutcome_IsNotAnError(PipelineOutcome outcome, int eventId, LogLevel level)
    {
        var (behavior, logger) = CreateLoggingBehavior();
        var exception = PipelineOutcomes.Create(outcome);

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            _ =>
            {
                _time.Advance(HandlerTime);
                throw exception;
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(exception);
        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal((4000, LogLevel.Information), (eventId, level));
        logger.Entries[1].Exception.Should().BeNull("the stack trace is the pipeline's own and tells an operator nothing");
        logger.Entries[1].Message.Should().Contain(nameof(TestCommand)).And.Contain(exception.GetType().Name).And.Contain("42ms");
    }

    // -------------------------------------------------------------------------------------------------------------
    // StreamLoggingBehavior<TRequest, TItem>
    // -------------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Stream logging behavior passes every item through unchanged")]
    public async Task StreamLogging_PassesItemsThrough()
    {
        var (behavior, _) = CreateStreamLoggingBehavior();
        var source = new[] { 10, 20, 30 };
        var nextCalled = 0;

        var collected = await Drain(behavior.Handle(
            new TestStreamRequest(0),
            ct =>
            {
                nextCalled++;
                return Produce(source, ct);
            },
            CancellationToken.None));

        nextCalled.Should().Be(1, "the inner stream factory is invoked exactly once during enumeration");
        collected.Should().Equal(source, "items must flow through the behavior unchanged and in order");
    }

    [Fact(DisplayName = "Stream logging behavior logs once on start and once on completion with the item count and elapsed time")]
    public async Task StreamLogging_OnSuccess_LogsAroundEnumeration()
    {
        var (behavior, logger) = CreateStreamLoggingBehavior();

        var enumerated = behavior.Handle(
            new TestStreamRequest(0),
            ct => AdvanceThenProduce([1, 2], ct),
            CancellationToken.None);

        // The start log only happens once enumeration begins (it lives inside the async iterator).
        logger.Entries.Should().BeEmpty("nothing is logged until the stream is enumerated");

        await Drain(enumerated);

        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal(
            (4003, LogLevel.Information), (4004, LogLevel.Information));
        logger.Entries[0].Message.Should().Be($"Streaming {nameof(TestStreamRequest)}");
        logger.Entries[1].Message.Should().Be($"Streamed {nameof(TestStreamRequest)}: 2 item(s) in 42ms");
    }

    [Fact(DisplayName = "Stream logging behavior logs an error with the item count and propagates when the inner stream throws mid-enumeration")]
    public async Task StreamLogging_OnFailure_LogsError_AndPropagates()
    {
        var (behavior, logger) = CreateStreamLoggingBehavior();
        var boom = new InvalidOperationException("stream-boom");

        Func<Task> act = () => Drain(behavior.Handle(new TestStreamRequest(0), ct => ThrowsAt([1], boom, ct), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stream-boom");

        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal(
            (4003, LogLevel.Information), (4005, LogLevel.Error));
        logger.Entries[1].Message.Should().Be($"Streaming {nameof(TestStreamRequest)} failed after 1 item(s) and 0ms");
        logger.Entries[1].Exception.Should().BeSameAs(boom);
    }

    [Fact(DisplayName = "Stream logging behavior logs caller cancellation at Information, not as an error")]
    public async Task StreamLogging_CallerCancellation_IsNotAnError()
    {
        var (behavior, logger) = CreateStreamLoggingBehavior();
        using var caller = new CancellationTokenSource();

        Func<Task> act = () => Drain(behavior.Handle(new TestStreamRequest(0), ct => CancelAfterFirst(caller, ct), caller.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4007 && e.Level == LogLevel.Information)
            .Which.Message.Should().Contain("1 item(s)");
    }

    [Theory(DisplayName = "Stream logging behavior logs an outcome the pipeline produces on purpose in one line below Error, with the item count")]
    [InlineData(PipelineOutcome.Invalid, 4010, LogLevel.Information)]
    [InlineData(PipelineOutcome.Duplicate, 4010, LogLevel.Information)]
    [InlineData(PipelineOutcome.KeyReused, 4010, LogLevel.Information)]
    [InlineData(PipelineOutcome.RateLimited, 4010, LogLevel.Information)]
    [InlineData(PipelineOutcome.TimedOut, 4011, LogLevel.Warning)]
    [InlineData(PipelineOutcome.QueueRefused, 4011, LogLevel.Warning)]
    public async Task StreamLogging_PipelineOutcome_IsNotAnError(PipelineOutcome outcome, int eventId, LogLevel level)
    {
        var (behavior, logger) = CreateStreamLoggingBehavior();
        var exception = PipelineOutcomes.Create(outcome);

        Func<Task> act = () => Drain(behavior.Handle(new TestStreamRequest(0), ct => ThrowsAt([1, 2], exception, ct), CancellationToken.None));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(exception);
        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal((4003, LogLevel.Information), (eventId, level));
        logger.Entries[1].Exception.Should().BeNull("the stack trace is the pipeline's own and tells an operator nothing");
        logger.Entries[1].Message.Should().Contain(exception.GetType().Name).And.Contain("2 item(s)");
    }

    // -------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------

    // The stream's own work takes HandlerTime of virtual time before its items flow.
    private async IAsyncEnumerable<int> AdvanceThenProduce(int[] items, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _time.Advance(HandlerTime);
        await foreach (var item in Produce(items, cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    private static async IAsyncEnumerable<int> CancelAfterFirst(CancellationTokenSource caller,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return 1;
        caller.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 2;
    }
}

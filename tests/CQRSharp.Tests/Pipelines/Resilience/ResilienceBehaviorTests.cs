using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Regression tests for the resilience behavior's retry policy: retries are opt-in via
///     <see cref="IRetryableRequest" />, verdicts (caller cancellation, the request's own timeout, a rate-limit rejection,
///     a duplicate, a validation failure) are never retried, transient faults (a dependency's timeout included) are, and
///     each retry waits out its back-off on the injected clock.
/// </summary>
public class ResilienceBehaviorTests
{
    private readonly RecordingTimeProvider _time = new();

    private ResilienceBehavior<TRequest, object> CreateBehavior<TRequest>(
        int maxRetries = 3,
        TimeSpan baseDelay = default,
        double backoffMultiplier = 1,
        ILogger<ResilienceBehavior<TRequest, object>>? logger = null)
        where TRequest : IRequest
        => new(
            logger ?? NullLogger<ResilienceBehavior<TRequest, object>>.Instance,
            Options.Create(new ResilienceOptions
            {
                MaxRetries = maxRetries,
                BaseDelay = baseDelay,
                BackoffMultiplier = backoffMultiplier,
                MaxDelay = TimeSpan.FromMinutes(1)
            }),
            _time);

    [Fact(DisplayName = "Non-retryable request: handler failure is propagated without retrying")]
    public async Task NonRetryable_DoesNotRetry()
    {
        var behavior = CreateBehavior<PlainRequest>();
        var calls = 0;

        Func<Task> act = () => behavior.Handle(
            new PlainRequest(),
            _ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        calls.Should().Be(1, "a non-IRetryableRequest must not be retried");
    }

    [Fact(DisplayName = "Retryable request: transient failures are retried until success")]
    public async Task Retryable_RetriesUntilSuccess()
    {
        var behavior = CreateBehavior<RetryableRequest>(3);
        var calls = 0;

        var result = await behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("transient");
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(3);
    }

    [Fact(DisplayName = "Retryable request: once MaxRetries retries have failed, the last failure propagates")]
    public async Task Retryable_RetriesExhausted_Throws()
    {
        var behavior = CreateBehavior<RetryableRequest>(2);
        var calls = 0;
        var failures = new List<Exception>();

        Func<Task> act = () => behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                var failure = new InvalidOperationException($"attempt {calls}");
                failures.Add(failure);
                throw failure;
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failures[^1]);
        calls.Should().Be(3, "the first attempt and MaxRetries (2) retries");
    }

    [Fact(DisplayName = "A retry waits out its back-off on the injected clock before the next attempt")]
    public async Task Retry_waits_for_the_back_off()
    {
        // A multiplier of 2 makes the retry number visible in the delay: the first retry waits BaseDelay, not twice it.
        var behavior = CreateBehavior<RetryableRequest>(1, TimeSpan.FromSeconds(1), backoffMultiplier: 2);
        var calls = 0;

        var handling = behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("transient");
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        // The first attempt failed synchronously, so the back-off is already waiting.
        _time.TimerDueTimes.Should().Equal([TimeSpan.FromSeconds(1)], "the back-off waits BaseDelay on the injected clock");

        _time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        calls.Should().Be(1, "the back-off has not elapsed");
        handling.IsCompleted.Should().BeFalse();

        _time.Advance(TimeSpan.FromTicks(1));

        (await handling).Should().Be("ok");
        calls.Should().Be(2);
    }

    [Fact(DisplayName = "Caller cancellation during a back-off ends the request without another attempt")]
    public async Task Cancellation_during_back_off_stops_retrying()
    {
        var behavior = CreateBehavior<RetryableRequest>(1, TimeSpan.FromSeconds(1));
        var calls = 0;
        using var caller = new CancellationTokenSource();

        var handling = behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                throw new InvalidOperationException("transient");
            },
            caller.Token);

        _time.TimerDueTimes.Should().ContainSingle("the back-off is waiting");
        await caller.CancelAsync();

        // A back-off that ignored the caller's token would now run the second attempt.
        _time.Advance(TimeSpan.FromSeconds(1));

        await FluentActions.Awaiting(() => handling).Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
    }

    [Fact(DisplayName = "Caller cancellation is never retried, even for a retryable request")]
    public async Task Cancellation_IsNotRetried()
    {
        var behavior = CreateBehavior<RetryableRequest>();
        var calls = 0;
        using var caller = new CancellationTokenSource();

        Func<Task> act = () => behavior.Handle(
            new RetryableRequest(),
            ct =>
            {
                calls++;
                caller.Cancel();
                throw new OperationCanceledException(ct);
            },
            caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1, "cancellation is terminal and must not be retried");
    }

    [Fact(DisplayName = "A cancellation the caller did not request (e.g. an HttpClient timeout) is a transient fault and is retried")]
    public async Task NonCallerCancellation_IsRetried()
    {
        var behavior = CreateBehavior<RetryableRequest>();
        var calls = 0;

        var result = await behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                if (calls == 1) throw new TaskCanceledException("simulated HttpClient timeout");
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(2, "the caller's token was never cancelled, so the fault is retryable");
    }

    [Theory(DisplayName = "A verdict is never retried, even for a retryable request")]
    [MemberData(nameof(RetryVerdicts.Names), MemberType = typeof(RetryVerdicts))]
    public async Task Verdicts_AreNotRetried(string verdict)
    {
        var behavior = CreateBehavior<RetryableRequest>();
        var failure = RetryVerdicts.Create(verdict, typeof(RetryableRequest));
        var calls = 0;

        Func<Task> act = () => behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                throw failure;
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        calls.Should().Be(1, "another attempt gets the same answer, so the back-off schedule is not spent on it");
    }

    [Fact(DisplayName = "A TimeoutException from a dependency is a transient fault and is retried")]
    public async Task DependencyTimeout_IsRetried()
    {
        var behavior = CreateBehavior<RetryableRequest>();
        var calls = 0;

        var result = await behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                if (calls == 1) throw new TimeoutException("simulated Redis timeout");
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(2, "only the timeout behavior's own RequestTimeoutException is terminal");
    }

    [Fact(DisplayName = "A request that is not retryable passes straight through: no log, no span")]
    public async Task NonRetryable_is_a_pass_through()
    {
        var logger = new CapturingLogger<ResilienceBehavior<PassThroughProbeRequest, object>>();
        var behavior = CreateBehavior(logger: logger);
        var spans = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsTelemetry.PipelinesActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (Equals(activity.GetTagItem(CqrsTelemetry.Tags.RequestType), typeof(PassThroughProbeRequest).FullName))
                    Interlocked.Increment(ref spans);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var boom = new InvalidOperationException("boom");

        Func<Task> act = () => behavior.Handle(new PassThroughProbeRequest(), _ => throw boom, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        logger.Entries.Should().BeEmpty("the behavior has nothing to say about a request it never retries");
        spans.Should().Be(0);
    }

    [Fact(DisplayName = "Retries are logged at Warning with the exception; giving up is one Information line; nothing at Error")]
    public async Task Retries_are_warnings_and_giving_up_is_information()
    {
        var logger = new CapturingLogger<ResilienceBehavior<RetryableRequest, object>>();
        var behavior = CreateBehavior(2, logger: logger);

        Func<Task> act = () => behavior.Handle(new RetryableRequest(), _ => throw new InvalidOperationException("always"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal(
            (4300, LogLevel.Warning), (4300, LogLevel.Warning), (4301, LogLevel.Information));
        logger.Entries.Take(2).Should().OnlyContain(e => e.Exception is InvalidOperationException);
        logger.Entries.Last().Exception.Should().BeNull("the exception propagates and is reported once, elsewhere");
        logger.Entries.Last().Message.Should().Contain("3 attempt(s)");
    }

    [Fact(DisplayName = "A non-retryable outcome of a retryable request is logged at Debug only")]
    public async Task Terminal_outcome_is_logged_at_debug()
    {
        var logger = new CapturingLogger<ResilienceBehavior<RetryableRequest, object>>();
        var behavior = CreateBehavior(2, logger: logger);

        Func<Task> act = () => behavior.Handle(
            new RetryableRequest(),
            _ => throw new RequestValidationException(typeof(RetryableRequest), Array.Empty<ValidationFailure>()),
            CancellationToken.None);

        await act.Should().ThrowAsync<RequestValidationException>();
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4302 && e.Level == LogLevel.Debug && e.Exception == null);
    }

    [Fact(DisplayName = "One failure through the logging and resilience behaviors is logged at Error exactly once")]
    public async Task One_failure_one_error()
    {
        var loggingLogger = new CapturingLogger<LoggingBehavior<RetryableRequest, object>>();
        var resilienceLogger = new CapturingLogger<ResilienceBehavior<RetryableRequest, object>>();
        var logging = new LoggingBehavior<RetryableRequest, object>(loggingLogger, _time);
        var resilience = CreateBehavior(1, logger: resilienceLogger);
        var request = new RetryableRequest();

        Func<Task> act = () => logging.Handle(
            request,
            ct => resilience.Handle(request, _ => throw new InvalidOperationException("fault"), ct),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        loggingLogger.Entries.Concat(resilienceLogger.Entries).Where(e => e.Level >= LogLevel.Error)
            .Should().ContainSingle().Which.EventId.Id.Should().Be(4002);
    }

    private sealed class PlainRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
    }

    private sealed class RetryableRequest : IRetryableRequest
    {
        public IRequestContext? Context { get; set; }
    }

    private sealed class PassThroughProbeRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
    }
}

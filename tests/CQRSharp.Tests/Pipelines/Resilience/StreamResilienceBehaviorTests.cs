using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for <see cref="StreamResilienceBehavior{TRequest,TItem}" />: it retries only an
///     <see cref="IRetryableRequest" /> that fails before yielding any item, after waiting out the back-off on the
///     injected clock; verdicts, caller cancellation, post-yield faults and non-retryable requests are all terminal.
/// </summary>
public class StreamResilienceBehaviorTests
{
    private readonly RecordingTimeProvider _time = new();

    private StreamResilienceBehavior<TRequest, int> CreateResilienceBehavior<TRequest>(int maxRetries = 3, TimeSpan baseDelay = default)
        where TRequest : IRequest
        => new(
            NullLogger<StreamResilienceBehavior<TRequest, int>>.Instance,
            Options.Create(new ResilienceOptions { MaxRetries = maxRetries, BaseDelay = baseDelay }),
            _time);

    [Fact(DisplayName = "Stream resilience: a successful stream passes its items through unchanged")]
    public async Task Resilience_Success_PassesThrough()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();

        var items = await Drain(behavior.Handle(new StreamRetryableRequest(), ct => Produce([0, 1, 2], ct), CancellationToken.None));

        items.Should().Equal(0, 1, 2);
    }

    [Fact(DisplayName = "Stream resilience: a retryable stream failing before any item is retried until it succeeds")]
    public async Task Resilience_Retryable_RetriesUntilSuccess_WhenFailsBeforeYield()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(3);
        var attempts = 0;

        var items = await Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct => ++attempts < 3 ? ThrowsAt([], new InvalidOperationException("transient"), ct) : Produce([0, 1], ct),
            CancellationToken.None));

        items.Should().Equal(0, 1);
        attempts.Should().Be(3, "the stream failed before yielding on the first two attempts and is retried");
    }

    [Fact(DisplayName = "Stream resilience: a retryable stream still failing before yield after all retries propagates the failure")]
    public async Task Resilience_Retryable_RetriesExhausted_Throws()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(2);
        var attempts = 0;

        Func<Task> act = () => Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct =>
            {
                attempts++;
                return ThrowsAt([], new InvalidOperationException("always"), ct);
            },
            CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("always");
        attempts.Should().Be(3, "the first attempt and MaxRetries (2) retries");
    }

    [Fact(DisplayName = "Stream resilience: a retry waits out its back-off on the injected clock before the next attempt")]
    public async Task Resilience_Retry_waits_for_the_back_off()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(1, TimeSpan.FromSeconds(1));
        var attempts = 0;

        var draining = Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct => ++attempts == 1 ? ThrowsAt([], new InvalidOperationException("transient"), ct) : Produce([7], ct),
            CancellationToken.None));

        // The first attempt failed on its first MoveNextAsync, so the back-off is already waiting.
        _time.TimerDueTimes.Should().Equal([TimeSpan.FromSeconds(1)], "the back-off waits BaseDelay on the injected clock");

        _time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        attempts.Should().Be(1, "the back-off has not elapsed");
        draining.IsCompleted.Should().BeFalse();

        _time.Advance(TimeSpan.FromTicks(1));

        (await draining).Should().Equal(7);
        attempts.Should().Be(2);
    }

    [Fact(DisplayName = "Stream resilience: caller cancellation during a back-off ends the stream without another attempt")]
    public async Task Resilience_Cancellation_during_back_off_stops_retrying()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(1, TimeSpan.FromSeconds(1));
        var attempts = 0;
        using var caller = new CancellationTokenSource();

        var draining = Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct =>
            {
                attempts++;
                return ThrowsAt([], new InvalidOperationException("transient"), ct);
            },
            caller.Token));

        _time.TimerDueTimes.Should().ContainSingle("the back-off is waiting");
        await caller.CancelAsync();

        // A back-off that ignored the caller's token would now run the second attempt.
        _time.Advance(TimeSpan.FromSeconds(1));

        await FluentActions.Awaiting(() => draining).Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1);
    }

    [Theory(DisplayName = "Stream resilience: a verdict is never retried, even for a retryable request")]
    [MemberData(nameof(RetryVerdicts.Names), MemberType = typeof(RetryVerdicts))]
    public async Task Resilience_Verdicts_AreNotRetried(string verdict)
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(3);
        var failure = RetryVerdicts.Create(verdict, typeof(StreamRetryableRequest));
        var attempts = 0;

        Func<Task> act = () => Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct =>
            {
                attempts++;
                return ThrowsAt([], failure, ct);
            },
            CancellationToken.None));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        attempts.Should().Be(1, "a verdict is propagated at once rather than retried through the back-off schedule");
    }

    [Fact(DisplayName = "Stream resilience: a non-retryable stream failure propagates without retrying")]
    public async Task Resilience_NonRetryable_DoesNotRetry()
    {
        var behavior = CreateResilienceBehavior<StreamPlainRequest>();
        var attempts = 0;

        Func<Task> act = () => Drain(behavior.Handle(
            new StreamPlainRequest(),
            ct =>
            {
                attempts++;
                return ThrowsAt([], new InvalidOperationException("boom"), ct);
            },
            CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        attempts.Should().Be(1, "a non-IRetryableRequest stream must not be retried");
    }

    [Fact(DisplayName = "Stream resilience: a retryable stream that faults AFTER yielding items is not retried (avoids duplicates)")]
    public async Task Resilience_Retryable_FaultAfterYield_IsNotRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;
        var seen = new List<int>();

        var act = async () =>
        {
            await foreach (var item in behavior.Handle(
                               new StreamRetryableRequest(),
                               ct =>
                               {
                                   attempts++;
                                   return ThrowsAt([0], new InvalidOperationException("post-yield"), ct);
                               },
                               CancellationToken.None).ConfigureAwait(false))
                seen.Add(item);
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("post-yield");
        attempts.Should().Be(1, "retries are disabled once items have been yielded to avoid duplicate partial results");
        seen.Should().Equal([0], "the item yielded before the fault is still observed by the consumer");
    }

    [Fact(DisplayName = "Stream resilience: caller cancellation is never retried, even for a retryable request")]
    public async Task Resilience_Cancellation_IsNotRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;
        using var caller = new CancellationTokenSource();

        Func<Task> act = () => Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct =>
            {
                attempts++;
                caller.Cancel();
                return ThrowsAt([], new OperationCanceledException(ct), ct);
            },
            caller.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1, "cancellation is terminal and must not be retried");
    }

    [Fact(DisplayName = "Stream resilience: a TimeoutException from a dependency before any item is retried")]
    public async Task Resilience_DependencyTimeout_IsRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;

        var items = await Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct => ++attempts == 1 ? ThrowsAt([], new TimeoutException("simulated Redis timeout"), ct) : Produce([7], ct),
            CancellationToken.None));

        items.Should().Equal(7);
        attempts.Should().Be(2, "only the timeout behavior's own RequestTimeoutException is terminal");
    }

    /// <summary>An opt-in retryable streaming request.</summary>
    private sealed class StreamRetryableRequest : IRetryableRequest
    {
        public IRequestContext? Context { get; set; }
    }
}

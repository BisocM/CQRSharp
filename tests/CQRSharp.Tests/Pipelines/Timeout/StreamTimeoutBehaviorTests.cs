using System.Runtime.CompilerServices;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for <see cref="StreamTimeoutBehavior{TRequest,TItem}" />: the budget bounds the whole enumeration
///     (not each item), a stream within it passes through, and caller cancellation is never mapped to a timeout. The
///     budget runs on a <see cref="FakeTimeProvider" />, so it expires exactly when a test advances the clock past it.
/// </summary>
public class StreamTimeoutBehaviorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLogger<StreamTimeoutBehavior<StreamPlainRequest, int>> _logger = new();

    private StreamTimeoutBehavior<StreamPlainRequest, int> CreateTimeoutBehavior()
        => new(_logger, Options.Create(new TimeoutOptions { Timeout = Budget }), _time);

    [Fact(DisplayName = "Stream timeout: a stream completing within the budget passes all items through")]
    public async Task Timeout_WithinBudget_PassesStreamThrough()
    {
        var behavior = CreateTimeoutBehavior();

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var item in Produce([0, 1, 2], ct).ConfigureAwait(false))
            {
                _time.Advance((Budget - TimeSpan.FromTicks(1)) / 3);
                yield return item;
            }
        }

        var items = await Drain(behavior.Handle(new StreamPlainRequest(), Source, CancellationToken.None));

        items.Should().Equal(0, 1, 2);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "Stream timeout: a stream exceeding the budget is cancelled and surfaced as RequestTimeoutException")]
    public async Task Timeout_ExceedingBudget_ThrowsRequestTimeoutException()
    {
        var behavior = CreateTimeoutBehavior();
        var producerWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken streamToken = default;

        async IAsyncEnumerable<int> Slow([EnumeratorCancellation] CancellationToken ct = default)
        {
            streamToken = ct;
            yield return 0;
            producerWaiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield return 1;
        }

        var draining = Drain(behavior.Handle(new StreamPlainRequest(), Slow, CancellationToken.None));
        await producerWaiting.Task;

        _time.Advance(Budget - TimeSpan.FromTicks(1));
        draining.IsCompleted.Should().BeFalse("the budget has not run out yet");
        streamToken.IsCancellationRequested.Should().BeFalse();

        _time.Advance(TimeSpan.FromTicks(1));
        streamToken.IsCancellationRequested.Should().BeTrue("the injected clock reached the budget");

        var exception = (await FluentActions.Awaiting(() => draining).Should().ThrowExactlyAsync<RequestTimeoutException>()).Which;
        exception.RequestType.Should().Be<StreamPlainRequest>();
        exception.Timeout.Should().Be(Budget);
        exception.InnerException.Should().BeAssignableTo<OperationCanceledException>("the cancellation the stream observed is kept");
        _logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4401 && e.Level == LogLevel.Information && e.Exception == null);
    }

    [Fact(DisplayName = "Stream timeout: the budget covers the whole enumeration, so items that each arrive in time still time out together")]
    public async Task Timeout_BoundsTheWholeEnumeration()
    {
        var behavior = CreateTimeoutBehavior();
        var atGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingForLast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken streamToken = default;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            streamToken = ct;
            yield return 0;
            atGate.SetResult();
            await gate.Task;
            yield return 1;
            // Reached only once the consumer has asked for the next item after the second.
            waitingForLast.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield return 2;
        }

        var draining = Drain(behavior.Handle(new StreamPlainRequest(), Source, CancellationToken.None));
        await atGate.Task;

        // Three quarters of the budget pass before the second item, and the third is requested after it: no single wait
        // comes near the budget.
        _time.Advance(Budget * 3 / 4);
        gate.SetResult();
        await waitingForLast.Task;
        streamToken.IsCancellationRequested.Should().BeFalse();

        // The last quarter runs out the budget, measured from the start of the enumeration.
        _time.Advance(Budget / 4);
        streamToken.IsCancellationRequested.Should().BeTrue("the budget is not restarted for each item");

        await FluentActions.Awaiting(() => draining).Should().ThrowExactlyAsync<RequestTimeoutException>();
    }

    [Fact(DisplayName = "Stream timeout: caller cancellation surfaces as OperationCanceledException, not as a timeout")]
    public async Task Timeout_CallerCancellation_IsNotMappedToTimeout()
    {
        var behavior = CreateTimeoutBehavior();
        using var caller = new CancellationTokenSource();
        var producerWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<int> Slow([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 0;
            producerWaiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield return 1;
        }

        var draining = Drain(behavior.Handle(new StreamPlainRequest(), Slow, caller.Token));
        await producerWaiting.Task;
        await caller.CancelAsync();

        // An OperationCanceledException assertion also rules out RequestTimeoutException, a TimeoutException.
        await FluentActions.Awaiting(() => draining).Should().ThrowAsync<OperationCanceledException>();
        _logger.Entries.Should().BeEmpty("nothing timed out");
    }
}

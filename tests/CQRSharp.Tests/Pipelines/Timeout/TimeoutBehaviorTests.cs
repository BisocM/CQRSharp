using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for the timeout pipeline behavior: in-budget work succeeds; over-budget work is cancelled and
///     surfaced as a <see cref="RequestTimeoutException" />; caller cancellation is never mapped to a timeout. The
///     budget runs on a <see cref="FakeTimeProvider" />, so it expires exactly when a test advances the clock past it.
/// </summary>
public class TimeoutBehaviorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLogger<TimeoutBehavior<TestCommand, object>> _logger = new();

    private TimeoutBehavior<TestCommand, object> CreateTimeoutBehavior()
        => new(_logger, Options.Create(new TimeoutOptions { Timeout = Budget }), _time);

    [Fact(DisplayName = "Timeout: work completing within the budget passes the result through")]
    public async Task Timeout_WithinBudget_PassesResultThrough()
    {
        var behavior = CreateTimeoutBehavior();

        var result = await behavior.Handle(
            new TestCommand(),
            _ =>
            {
                _time.Advance(Budget - TimeSpan.FromTicks(1));
                return Task.FromResult<object>("done");
            },
            CancellationToken.None);

        result.Should().Be("done");
        _logger.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "Timeout: work exceeding the budget is cancelled and surfaced as RequestTimeoutException naming the request and budget")]
    public async Task Timeout_ExceedingBudget_ThrowsRequestTimeoutException()
    {
        var behavior = CreateTimeoutBehavior();
        CancellationToken handlerToken = default;

        var handling = behavior.Handle(
            new TestCommand(),
            async ct =>
            {
                handlerToken = ct;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "never";
            },
            CancellationToken.None);

        // The handler is waiting on its token; only the injected clock can end the wait.
        _time.Advance(Budget - TimeSpan.FromTicks(1));
        handling.IsCompleted.Should().BeFalse("the budget has not run out yet");
        handlerToken.IsCancellationRequested.Should().BeFalse();

        _time.Advance(TimeSpan.FromTicks(1));
        handlerToken.IsCancellationRequested.Should().BeTrue("the injected clock reached the budget");

        var exception = (await FluentActions.Awaiting(() => handling).Should().ThrowExactlyAsync<RequestTimeoutException>()).Which;
        exception.RequestType.Should().Be<TestCommand>();
        exception.Timeout.Should().Be(Budget);
        exception.InnerException.Should().BeAssignableTo<OperationCanceledException>("the cancellation the handler observed is kept");
        _logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4400 && e.Level == LogLevel.Information && e.Exception == null);
    }

    [Fact(DisplayName = "Timeout: caller cancellation surfaces as OperationCanceledException, not as a timeout")]
    public async Task Timeout_CallerCancellation_IsNotMappedToTimeout()
    {
        var behavior = CreateTimeoutBehavior();
        using var caller = new CancellationTokenSource();

        var handling = behavior.Handle(
            new TestCommand(),
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "never";
            },
            caller.Token);

        await caller.CancelAsync();

        // An OperationCanceledException assertion also rules out RequestTimeoutException, a TimeoutException.
        await FluentActions.Awaiting(() => handling).Should().ThrowAsync<OperationCanceledException>();
        _logger.Entries.Should().BeEmpty("nothing timed out");
    }
}

using CQRSharp.Abstractions.Interfaces.Validation;
using CQRSharp.Abstractions.Models.Validation;
using CQRSharp.Pipelines.Behaviors.Timeout;
using CQRSharp.Pipelines.Behaviors.Validation;
using CQRSharp.Pipelines.Options;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for the validation pipeline behavior (failures abort the pipeline with a
///     <see cref="RequestValidationException" />; success passes the result through) and the timeout
///     pipeline behavior (in-budget work succeeds; over-budget work is cancelled and surfaced as a
///     <see cref="TimeoutException" />). Timings are kept short and cancellation-honoring to stay
///     deterministic.
/// </summary>
public class ValidationAndTimeoutBehaviorTests
{
    private static ValidationBehavior<TestCommand, object> CreateValidationBehavior(
        params IRequestValidator<TestCommand>[] validators)
        => new(validators);

    [Fact(DisplayName = "Validation: validator failures throw RequestValidationException and short-circuit next")]
    public async Task Validation_WithFailures_Throws_AndDoesNotCallNext()
    {
        var f1 = new ValidationFailure("E1", "First failure", "Name");
        var f2 = new ValidationFailure("E2", "Second failure");
        var behavior = CreateValidationBehavior(new FakeValidator(f1, f2));
        var nextCalled = false;

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<object>("should-not-run");
            },
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<RequestValidationException>();
        assertion.Which.RequestType.Should().Be(typeof(TestCommand));
        assertion.Which.Failures.Should().BeEquivalentTo(new[] { f1, f2 });
        nextCalled.Should().BeFalse("a failing validation must short-circuit the pipeline");
    }

    [Fact(DisplayName = "Validation: failures from multiple validators are aggregated into the exception")]
    public async Task Validation_AggregatesFailuresAcrossValidators()
    {
        var f1 = new ValidationFailure("E1", "From validator one");
        var f2 = new ValidationFailure("E2", "From validator two");
        var behavior = CreateValidationBehavior(new FakeValidator(f1), new FakeValidator(f2));

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            _ => Task.FromResult<object>("unused"),
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<RequestValidationException>();
        assertion.Which.Failures.Should().BeEquivalentTo(new[] { f1, f2 });
    }

    [Fact(DisplayName = "Validation: a passing validator lets next run and passes the result through")]
    public async Task Validation_NoFailures_CallsNext_AndPassesResultThrough()
    {
        var validator = new FakeValidator(); // no failures
        var behavior = CreateValidationBehavior(validator);
        var nextCalled = false;

        var result = await behavior.Handle(
            new TestCommand(),
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        validator.WasCalled.Should().BeTrue();
        nextCalled.Should().BeTrue();
        result.Should().Be("ok");
    }

    [Fact(DisplayName = "Validation: with no registered validators next runs and the result passes through")]
    public async Task Validation_NoValidators_CallsNext_AndPassesResultThrough()
    {
        var behavior = CreateValidationBehavior(); // empty validator collection
        var nextCalled = false;

        var result = await behavior.Handle(
            new TestCommand(),
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<object>("passthrough");
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("passthrough");
    }

    // ---- Timeout ----------------------------------------------------------

    private static TimeoutBehavior<TestCommand, object> CreateTimeoutBehavior(TimeSpan timeout)
        => new(
            NullLogger<TimeoutBehavior<TestCommand, object>>.Instance,
            Options.Create(new TimeoutOptions { Timeout = timeout }));

    [Fact(DisplayName = "Timeout: work completing within the budget passes the result through")]
    public async Task Timeout_WithinBudget_PassesResultThrough()
    {
        // Generous budget; next completes effectively immediately.
        var behavior = CreateTimeoutBehavior(TimeSpan.FromSeconds(30));

        var result = await behavior.Handle(
            new TestCommand(),
            _ => Task.FromResult<object>("done"),
            CancellationToken.None);

        result.Should().Be("done");
    }

    [Fact(DisplayName = "Timeout: work exceeding the budget is cancelled and surfaced as TimeoutException")]
    public async Task Timeout_ExceedingBudget_ThrowsTimeoutException()
    {
        // Very short budget; next honors the combined cancellation token and would otherwise
        // run far longer, so the timeout source fires first.
        var behavior = CreateTimeoutBehavior(TimeSpan.FromMilliseconds(20));

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return "never";
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact(DisplayName = "Timeout: caller cancellation surfaces as OperationCanceledException, not TimeoutException")]
    public async Task Timeout_CallerCancellation_IsNotMappedToTimeout()
    {
        // Generous timeout so the timeout source never fires; the caller cancels instead.
        var behavior = CreateTimeoutBehavior(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            async ct =>
            {
                // ReSharper disable once AccessToDisposedClosure
                cts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return "never";
            },
            cts.Token);

        // The catch filter only maps to TimeoutException when the timeout source fired; a caller
        // cancellation must propagate as OperationCanceledException.
        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.Should().NotBeOfType<TimeoutException>();
    }
    // ---- Validation -------------------------------------------------------

    /// <summary>
    ///     A configurable fake validator that returns a fixed set of failures and records whether it ran.
    /// </summary>
    private sealed class FakeValidator(params ValidationFailure[] failures)
        : IRequestValidator<TestCommand>
    {
        public bool WasCalled { get; private set; }

        public Task<ValidationFailure[]> ValidateAsync(TestCommand request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(failures);
        }
    }
}
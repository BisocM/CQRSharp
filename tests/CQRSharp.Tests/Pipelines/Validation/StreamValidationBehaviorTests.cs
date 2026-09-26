using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for the streaming validation behavior. Mirrors <see cref="ValidationBehaviorTests" /> but drives an
///     <see cref="IAsyncEnumerable{T}" /> next-delegate: an invalid request throws <see cref="RequestValidationException" />
///     before the inner stream is ever started; a valid request streams through unchanged.
/// </summary>
public sealed class StreamValidationBehaviorTests
{
    private static StreamValidationBehavior<TestCommand, int> CreateValidationBehavior(
        params IRequestValidator<TestCommand>[] validators)
        => new(validators);

    [Fact(DisplayName = "Stream Validation: validator failures throw RequestValidationException before the inner stream starts")]
    public async Task StreamValidation_WithFailures_Throws_AndNeverStartsInnerStream()
    {
        var f1 = new ValidationFailure("E1", "First failure", "Name");
        var f2 = new ValidationFailure("E2", "Second failure");
        var validator = new FakeValidator<TestCommand>(f1, f2);
        var behavior = CreateValidationBehavior(validator);
        var innerStarted = false;

        Func<Task> act = () => Drain(behavior.Handle(
            new TestCommand(),
            ct =>
            {
                innerStarted = true;
                return Produce([1], ct);
            },
            CancellationToken.None));

        var assertion = await act.Should().ThrowAsync<RequestValidationException>();
        assertion.Which.RequestType.Should().Be(typeof(TestCommand));
        assertion.Which.Failures.Should().BeEquivalentTo(new[] { f1, f2 });

        validator.WasCalled.Should().BeTrue();
        innerStarted.Should().BeFalse("a failing validation must short-circuit before the inner stream is requested");
    }

    [Fact(DisplayName = "Stream Validation: failures from multiple validators are aggregated into the exception")]
    public async Task StreamValidation_AggregatesFailuresAcrossValidators()
    {
        var f1 = new ValidationFailure("E1", "From validator one");
        var f2 = new ValidationFailure("E2", "From validator two");
        var behavior = CreateValidationBehavior(new FakeValidator<TestCommand>(f1), new FakeValidator<TestCommand>(f2));

        Func<Task> act = () => Drain(behavior.Handle(new TestCommand(), ct => Produce([1], ct), CancellationToken.None));

        var assertion = await act.Should().ThrowAsync<RequestValidationException>();
        assertion.Which.Failures.Should().BeEquivalentTo(new[] { f1, f2 });
    }

    [Fact(DisplayName = "Stream Validation: a passing validator lets the stream run and yields items unchanged")]
    public async Task StreamValidation_NoFailures_StreamsThrough()
    {
        var validator = new FakeValidator<TestCommand>(); // no failures
        var behavior = CreateValidationBehavior(validator);

        var collected = await Drain(behavior.Handle(new TestCommand(), ct => Produce([7, 8, 9], ct), CancellationToken.None));

        validator.WasCalled.Should().BeTrue();
        collected.Should().Equal(7, 8, 9);
    }

    [Fact(DisplayName = "Stream Validation: with no registered validators the stream runs and items pass through")]
    public async Task StreamValidation_NoValidators_StreamsThrough()
    {
        var behavior = CreateValidationBehavior(); // empty validator collection

        var collected = await Drain(behavior.Handle(new TestCommand(), ct => Produce([42], ct), CancellationToken.None));

        collected.Should().Equal(42);
    }
}

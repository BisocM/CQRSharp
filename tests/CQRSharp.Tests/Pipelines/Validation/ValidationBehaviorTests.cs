using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for the validation pipeline behavior: failures abort the pipeline with a
///     <see cref="RequestValidationException" />; success passes the result through.
/// </summary>
public class ValidationBehaviorTests
{
    private static ValidationBehavior<TestCommand, object> CreateValidationBehavior(
        params IRequestValidator<TestCommand>[] validators)
        => new(validators);

    [Fact(DisplayName = "Validation: validator failures throw RequestValidationException and short-circuit next")]
    public async Task Validation_WithFailures_Throws_AndDoesNotCallNext()
    {
        var f1 = new ValidationFailure("E1", "First failure", "Name");
        var f2 = new ValidationFailure("E2", "Second failure");
        var behavior = CreateValidationBehavior(new FakeValidator<TestCommand>(f1, f2));
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
        var behavior = CreateValidationBehavior(new FakeValidator<TestCommand>(f1), new FakeValidator<TestCommand>(f2));

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
        var validator = new FakeValidator<TestCommand>(); // no failures
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
}

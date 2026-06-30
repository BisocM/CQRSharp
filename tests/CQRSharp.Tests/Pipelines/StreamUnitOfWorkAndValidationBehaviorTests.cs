using System.Data;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Abstractions.Interfaces.Validation;
using CQRSharp.Abstractions.Models.Validation;
using CQRSharp.Pipelines.Behaviors.Transactions;
using CQRSharp.Pipelines.Behaviors.Validation;
using CQRSharp.Pipelines.Options;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for the streaming pipeline behaviors. Mirrors <see cref="UnitOfWorkBehaviorTests" /> and
///     <see cref="ValidationAndTimeoutBehaviorTests" /> but drives an <see cref="IAsyncEnumerable{T}" /> next-delegate.
///     <para>
///         Stream UoW: a transactional stream that enumerates fully commits the unit of work exactly once and the items
///         flow through unchanged; a stream that throws mid-enumeration rolls back (never commits) and the original
///         exception propagates.
///     </para>
///     <para>
///         Stream validation: an invalid request throws <see cref="RequestValidationException" /> before the inner stream
///         is ever started; a valid request streams through unchanged.
///     </para>
///     A plain <see cref="IUnitOfWork" /> mock (not <see cref="IExplicitUnitOfWork" />) exercises the implicit-transaction
///     path, where committing is a single <see cref="IUnitOfWork.SaveChangesAsync" /> call.
/// </summary>
public sealed class StreamUnitOfWorkAndValidationBehaviorTests
{
    // ---- Stream Unit of Work ---------------------------------------------

    private readonly Mock<ILogger<StreamUnitOfWorkBehavior<ITransactionalCommand, int>>> _mockLogger = new();
    private readonly Mock<IOutbox> _mockOutbox = new();
    private readonly Mock<IUnitOfWork> _mockUoW = new();

    private readonly IOptions<UnitOfWorkOptions> _options =
        Options.Create(new UnitOfWorkOptions { DefaultIsolationLevel = IsolationLevel.ReadCommitted });

    public StreamUnitOfWorkAndValidationBehaviorTests()
        => _mockOutbox.Setup(o => o.Drain()).Returns(Array.Empty<INotification>());

    private StreamUnitOfWorkBehavior<ITransactionalCommand, int> CreateUoWBehavior()
        => new(_mockLogger.Object, _mockUoW.Object, _mockOutbox.Object, _options);

    private static async IAsyncEnumerable<int> Range(params int[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<int> ThrowsAt(int yieldCount, Exception failure)
    {
        for (var i = 0; i < yieldCount; i++)
        {
            await Task.Yield();
            yield return i;
        }

        await Task.Yield();
        throw failure;
    }

    [Fact(DisplayName = "Stream UoW: a fully-enumerated transactional stream commits once and yields items unchanged")]
    public async Task StreamUoW_FullEnumeration_CommitsOnce_AndPassesItemsThrough()
    {
        var behavior = CreateUoWBehavior();
        var request = new TransactionalCommand();
        var nextCalls = 0;

        var collected = new List<int>();
        await foreach (var item in behavior.Handle(
                           request,
                           _ =>
                           {
                               nextCalls++;
                               return Range(10, 20, 30);
                           },
                           CancellationToken.None))
            collected.Add(item);

        collected.Should().Equal(10, 20, 30);
        nextCalls.Should().Be(1);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Stream UoW: a transactional stream that throws mid-enumeration rolls back (never commits) and rethrows")]
    public async Task StreamUoW_ThrowsMidStream_RollsBack_AndRethrows()
    {
        var behavior = CreateUoWBehavior();
        var request = new TransactionalCommand();
        var failure = new InvalidOperationException("Stream failed mid-enumeration");

        var collected = new List<int>();

        var act = async () =>
        {
            await foreach (var item in behavior.Handle(
                               request,
                               _ => ThrowsAt(2, failure),
                               CancellationToken.None))
                collected.Add(item);
        };

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Should().BeSameAs(failure);

        // Items produced before the failure still flowed through unchanged.
        collected.Should().Equal(0, 1);

        // Rollback for the implicit path means the commit (SaveChangesAsync) never happens.
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Stream UoW: a non-transactional request bypasses the UoW and never commits")]
    public async Task StreamUoW_NonTransactionalRequest_BypassesUoW()
    {
        // The behavior is generic over IRequest; a NonTransactionalCommand is neither
        // ITransactionalCommand nor ITransactionalQuery, so the UoW logic is bypassed entirely.
        var behavior = new StreamUnitOfWorkBehavior<NonTransactionalCommand, int>(
            new Mock<ILogger<StreamUnitOfWorkBehavior<NonTransactionalCommand, int>>>().Object,
            _mockUoW.Object,
            _mockOutbox.Object,
            _options);

        var collected = new List<int>();
        await foreach (var item in behavior.Handle(
                           new NonTransactionalCommand(),
                           _ => Range(1, 2),
                           CancellationToken.None))
            collected.Add(item);

        collected.Should().Equal(1, 2);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static StreamValidationBehavior<TestCommand, int> CreateValidationBehavior(
        params IRequestValidator<TestCommand>[] validators)
        => new(validators);

    [Fact(DisplayName = "Stream Validation: validator failures throw RequestValidationException before the inner stream starts")]
    public async Task StreamValidation_WithFailures_Throws_AndNeverStartsInnerStream()
    {
        var f1 = new ValidationFailure("E1", "First failure", "Name");
        var f2 = new ValidationFailure("E2", "Second failure");
        var validator = new StreamFakeValidator(f1, f2);
        var behavior = CreateValidationBehavior(validator);
        var innerStarted = false;

        async IAsyncEnumerable<int> Inner()
        {
            innerStarted = true;
            await Task.Yield();
            yield return 1;
        }

        var act = async () =>
        {
            await foreach (var _ in behavior.Handle(new TestCommand(), _ => Inner(), CancellationToken.None))
            {
                // The body must never run; the exception fires before the first MoveNextAsync of the inner stream.
            }
        };

        var assertion = await act.Should().ThrowAsync<RequestValidationException>();
        assertion.Which.RequestType.Should().Be(typeof(TestCommand));
        assertion.Which.Failures.Should().BeEquivalentTo(new[] { f1, f2 });

        validator.WasCalled.Should().BeTrue();
        innerStarted.Should().BeFalse("a failing validation must short-circuit before the inner stream is enumerated");
    }

    [Fact(DisplayName = "Stream Validation: failures from multiple validators are aggregated into the exception")]
    public async Task StreamValidation_AggregatesFailuresAcrossValidators()
    {
        var f1 = new ValidationFailure("E1", "From validator one");
        var f2 = new ValidationFailure("E2", "From validator two");
        var behavior = CreateValidationBehavior(new StreamFakeValidator(f1), new StreamFakeValidator(f2));

        var act = async () =>
        {
            await foreach (var _ in behavior.Handle(new TestCommand(), _ => Range(1), CancellationToken.None))
            {
            }
        };

        var assertion = await act.Should().ThrowAsync<RequestValidationException>();
        assertion.Which.Failures.Should().BeEquivalentTo(new[] { f1, f2 });
    }

    [Fact(DisplayName = "Stream Validation: a passing validator lets the stream run and yields items unchanged")]
    public async Task StreamValidation_NoFailures_StreamsThrough()
    {
        var validator = new StreamFakeValidator(); // no failures
        var behavior = CreateValidationBehavior(validator);

        var collected = new List<int>();
        await foreach (var item in behavior.Handle(new TestCommand(), _ => Range(7, 8, 9), CancellationToken.None))
            collected.Add(item);

        validator.WasCalled.Should().BeTrue();
        collected.Should().Equal(7, 8, 9);
    }

    [Fact(DisplayName = "Stream Validation: with no registered validators the stream runs and items pass through")]
    public async Task StreamValidation_NoValidators_StreamsThrough()
    {
        var behavior = CreateValidationBehavior(); // empty validator collection

        var collected = new List<int>();
        await foreach (var item in behavior.Handle(new TestCommand(), _ => Range(42), CancellationToken.None))
            collected.Add(item);

        collected.Should().Equal(42);
    }

    // ---- Stream Validation -----------------------------------------------

    /// <summary>
    ///     A configurable fake stream validator that returns a fixed set of failures and records whether it ran.
    ///     Uniquely named (Stream-prefixed, nested, private) so it never collides with the FakeValidator used by
    ///     <see cref="ValidationAndTimeoutBehaviorTests" />.
    /// </summary>
    private sealed class StreamFakeValidator(params ValidationFailure[] failures)
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
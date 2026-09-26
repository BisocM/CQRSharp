using System.Data;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The unit-of-work behavior over the one <c>IUnitOfWork</c> contract: which transaction it begins, when it commits,
///     when it rolls back (always under no token), when the request's notifications reach the store relative to the
///     commit, and when the processor is woken.
/// </summary>
public sealed class UnitOfWorkBehaviorTests
{
    private static Task<CommandResult> Succeed(UnitOfWorkHarness harness)
    {
        harness.Publish();
        return Task.FromResult(CommandResult.FromSuccess());
    }

    [Fact(DisplayName = "A transactional command commits once; a store that joins the transaction is written inside it, and the processor is woken after the commit")]
    public async Task Success_stores_inside_a_joining_transaction()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: true);

        var result = await harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        result.IsSuccess.Should().BeTrue();
        harness.Log.Entries.Should().Equal("begin", "store", "commit", "signal");
        harness.UnitOfWork.Rollbacks.Should().Be(0);
        harness.Store.Stored.Should().ContainSingle().Which.NotificationType.Should().Be("test.notification");
        harness.Outbox.Count.Should().Be(0, "the transaction took its request's notifications");
    }

    [Fact(DisplayName = "A store that does not join the transaction is written right after the commit, then the processor is woken")]
    public async Task Success_stores_after_the_commit_when_the_store_does_not_join()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: false);

        await harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        harness.Log.Entries.Should().Equal("begin", "commit", "store", "signal");
        harness.Store.Stored.Should().ContainSingle();
    }

    [Fact(DisplayName = "A commit that fails with a non-joining store stores nothing, rolls back under no token, and the next request begins its own transaction")]
    public async Task Failed_commit_publishes_nothing_and_leaves_the_scope_clean()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: false);
        harness.UnitOfWork.FailNextCommit(new IOException("serialization failure"));
        var behavior = harness.Behavior<TransactionalCommand, CommandResult>();

        var act = () => harness.AsRequest(() => behavior.Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        (await act.Should().ThrowAsync<IOException>()).WithMessage("serialization failure");
        harness.Log.Entries.Should().Equal("begin", "commit-failed", "rollback");
        harness.Store.Stored.Should().BeEmpty("a notification of work whose commit failed must never be published");
        harness.UnitOfWork.RollbackTokens.Should().Equal(CancellationToken.None);
        harness.UnitOfWork.HasActiveTransaction.Should().BeFalse();

        await harness.AsRequest(() => behavior.Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        harness.UnitOfWork.BeganWith.Should().HaveCount(2, "the second request began a transaction of its own rather than joining a dead one");
        harness.UnitOfWork.Commits.Should().Be(1);
        harness.Store.Stored.Should().ContainSingle("only the committed request's notification is stored");
    }

    [Fact(DisplayName = "A request retried after a failed commit stores exactly one copy of its notifications")]
    public async Task Retry_after_a_failed_commit_stores_one_copy()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: false);
        harness.UnitOfWork.FailNextCommit(new IOException("deadlock"));
        var behavior = harness.Behavior<TransactionalCommand, CommandResult>();

        // Resilience wraps the unit of work, so each attempt is one pass through the behavior within the same request.
        var result = await harness.AsRequest(async () =>
        {
            var request = new TransactionalCommand();
            try
            {
                await behavior.Handle(request, _ => Succeed(harness), CancellationToken.None);
            }
            catch (IOException)
            {
            }

            return await behavior.Handle(request, _ => Succeed(harness), CancellationToken.None);
        });

        result.IsSuccess.Should().BeTrue();
        harness.Store.Stored.Should().ContainSingle();
    }

    [Fact(DisplayName = "A store failure after the commit is logged with the notification names and never undoes the committed work")]
    public async Task Store_failure_after_the_commit_is_logged_not_rolled_back()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: false);
        harness.Store.StoreFailure = new IOException("store unreachable");
        var logger = new CapturingLogger<UnitOfWorkBehavior<TransactionalCommand, CommandResult>>();

        var result = await harness.AsRequest(() => harness.Behavior(logger)
            .Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        result.IsSuccess.Should().BeTrue("the committed work stands");
        harness.Log.Entries.Should().Equal("begin", "commit", "store-failed");
        harness.UnitOfWork.Rollbacks.Should().Be(0);
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4206)
            .Which.Should().Match<CapturedLogEntry>(e =>
                e.Level == LogLevel.Error && e.Message.Contains(nameof(TestNotification)) && e.Exception is IOException);
    }

    [Fact(DisplayName = "A joining store that fails before the commit rolls the transaction back and surfaces the failure")]
    public async Task Joining_store_failure_rolls_back()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: true);
        harness.Store.StoreFailure = new IOException("store failed");

        var act = () => harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        await act.Should().ThrowAsync<IOException>();
        harness.Log.Entries.Should().Equal("begin", "store-failed", "rollback");
        harness.UnitOfWork.Commits.Should().Be(0);
    }

    [Theory(DisplayName = "The transaction begins at the request's isolation level, else the configured default, else the data store's")]
    [InlineData(IsolationLevel.Serializable, IsolationLevel.ReadCommitted, IsolationLevel.Serializable)]
    [InlineData((IsolationLevel)0, IsolationLevel.RepeatableRead, IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Unspecified, IsolationLevel.RepeatableRead, IsolationLevel.RepeatableRead)]
    [InlineData((IsolationLevel)0, IsolationLevel.Unspecified, IsolationLevel.Unspecified)]
    [InlineData((IsolationLevel)12345, IsolationLevel.Unspecified, IsolationLevel.Unspecified)]
    public async Task Isolation_level_falls_back_to_the_default(IsolationLevel requested, IsolationLevel configuredDefault, IsolationLevel expected)
    {
        await using var harness = UnitOfWorkHarness.Create(configure: o => o.DefaultIsolationLevel = configuredDefault);

        await harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand { IsolationLevel = requested }, _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None));
        await harness.AsRequest(() => harness.Behavior<IsolatedQuery, int>()
            .Handle(new IsolatedQuery { IsolationLevel = requested }, _ => Task.FromResult(1), CancellationToken.None));

        harness.UnitOfWork.BeganWith.Should().Equal(expected, expected);
    }

    [Fact(DisplayName = "A configured default isolation level that names no level is rejected when the options are built")]
    public async Task Undefined_default_isolation_level_is_rejected()
    {
        await using var harness = UnitOfWorkHarness.Create(configure: o => o.DefaultIsolationLevel = (IsolationLevel)0);

        var act = () => harness.Services.GetRequiredService<IOptions<UnitOfWorkOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(nameof(UnitOfWorkOptions.DefaultIsolationLevel));
    }

    [Fact(DisplayName = "With a transaction already active, the request takes part in it: no begin, commit or rollback, and its notifications are left to the request")]
    public async Task Active_transaction_is_joined()
    {
        await using var harness = UnitOfWorkHarness.Create();
        harness.UnitOfWork.HasActiveTransaction = true;
        var ran = false;

        var result = await harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand(), _ =>
            {
                ran = true;
                return Succeed(harness);
            }, CancellationToken.None));

        result.IsSuccess.Should().BeTrue();
        ran.Should().BeTrue();
        harness.UnitOfWork.BeganWith.Should().BeEmpty();
        harness.UnitOfWork.Commits.Should().Be(0);
        harness.UnitOfWork.Rollbacks.Should().Be(0);
        harness.Log.Entries.Should().Equal(new[] { "store", "signal" }, "the request settled its notification itself, into the transaction it took part in");
    }

    [Fact(DisplayName = "A handler exception rolls back under no token even when the caller's token is cancelled, and discards the request's notifications")]
    public async Task Handler_exception_rolls_back_under_no_token()
    {
        await using var harness = UnitOfWorkHarness.Create();
        using var cts = new CancellationTokenSource();

        var act = () => harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand(), _ =>
            {
                harness.Publish();
                cts.Cancel();
                throw new InvalidOperationException("handler failed");
            }, cts.Token));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler failed");
        harness.Log.Entries.Should().Equal("begin", "rollback");
        harness.UnitOfWork.RollbackTokens.Should().Equal(CancellationToken.None);
        harness.Outbox.Count.Should().Be(0, "the rolled-back work's notification is discarded");
    }

    [Fact(DisplayName = "A failed result rolls back and discards the request's notifications; the result is returned, and the rollback is logged at Information")]
    public async Task Failed_result_rolls_back()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<UnitOfWorkBehavior<TransactionalCommand, CommandResult>>();

        var result = await harness.AsRequest(() => harness.Behavior(logger)
            .Handle(new TransactionalCommand(), _ =>
            {
                harness.Publish();
                return Task.FromResult(CommandResult.FromError("declined"));
            }, CancellationToken.None));

        result.ErrorMessage.Should().Be("declined");
        harness.Log.Entries.Should().Equal("begin", "rollback");
        harness.Outbox.Count.Should().Be(0);
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4203 && e.Level == LogLevel.Information && e.Exception == null);
    }

    [Fact(DisplayName = "With RollbackOnFailedResult off, a failed result is committed with its notifications")]
    public async Task Failed_result_is_committed_when_opted_out()
    {
        await using var harness = UnitOfWorkHarness.Create(configure: o => o.RollbackOnFailedResult = false);

        var result = await harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>()
            .Handle(new TransactionalCommand(), _ =>
            {
                harness.Publish();
                return Task.FromResult(CommandResult.FromError("recorded anyway"));
            }, CancellationToken.None));

        result.IsSuccess.Should().BeFalse();
        harness.Log.Entries.Should().Equal("begin", "store", "commit", "signal");
    }

    [Fact(DisplayName = "A read-only transactional query is rolled back, not committed, and its notifications are left for the request to settle")]
    public async Task Read_only_query_rolls_back()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var result = await harness.AsRequest(() => harness.Behavior<IsolatedQuery, int>()
            .Handle(new IsolatedQuery(), _ =>
            {
                harness.Publish();
                return Task.FromResult(7);
            }, CancellationToken.None));

        result.Should().Be(7);
        harness.Log.Entries.Should().Equal("begin", "rollback", "store", "signal");
    }

    [Fact(DisplayName = "A transactional query that writes is committed like a command")]
    public async Task Writing_query_commits()
    {
        await using var harness = UnitOfWorkHarness.Create();

        await harness.AsRequest(() => harness.Behavior<WritingQuery, int>()
            .Handle(new WritingQuery(), _ => Task.FromResult(1), CancellationToken.None));

        harness.Log.Entries.Should().Equal("begin", "commit");
    }

    [Fact(DisplayName = "A value-returning command opts into the transaction: committed on success, rolled back on a failed CommandResult<T>")]
    public async Task Value_returning_command_is_transactional()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var behavior = harness.Behavior<MintTokenCommand, CommandResult<Guid>>();

        var minted = await harness.AsRequest(() => behavior.Handle(new MintTokenCommand(), _ => Task.FromResult(CommandResult<Guid>.FromSuccess(Guid.NewGuid())), CancellationToken.None));
        var refused = await harness.AsRequest(() => behavior.Handle(new MintTokenCommand(), _ => Task.FromResult(CommandResult<Guid>.FromError("quota")), CancellationToken.None));

        minted.IsSuccess.Should().BeTrue();
        refused.IsSuccess.Should().BeFalse();
        harness.Log.Entries.Should().Equal("begin", "commit", "begin", "rollback");
        harness.UnitOfWork.BeganWith.Should().Equal(IsolationLevel.Serializable, IsolationLevel.Serializable);
    }

    [Fact(DisplayName = "A request that is not transactional passes straight through")]
    public async Task Non_transactional_request_bypasses_the_unit_of_work()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var result = await harness.Behavior<NonTransactionalCommand, CommandResult>()
            .Handle(new NonTransactionalCommand(), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        harness.Log.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "A rollback that fails after a read-only query surfaces once")]
    public async Task Failing_read_only_rollback_surfaces()
    {
        await using var harness = UnitOfWorkHarness.Create();
        harness.UnitOfWork.RollbackFailure = new IOException("rollback failed");

        var act = () => harness.AsRequest(() => harness.Behavior<IsolatedQuery, int>()
            .Handle(new IsolatedQuery(), _ => Task.FromResult(1), CancellationToken.None));

        (await act.Should().ThrowAsync<IOException>()).WithMessage("rollback failed");
        harness.UnitOfWork.Rollbacks.Should().Be(1, "the failed rollback is not retried");
    }

    [Fact(DisplayName = "A handler exception is logged once, as the rollback, below Error and without the exception it propagates")]
    public async Task Handler_exception_logs_the_rollback_not_the_exception()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<UnitOfWorkBehavior<TransactionalCommand, CommandResult>>();

        var act = () => harness.AsRequest(() => harness.Behavior(logger)
            .Handle(new TransactionalCommand(), _ => throw new InvalidOperationException("handler failed"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4201 && e.Level == LogLevel.Information && e.Exception == null &&
            e.Message.Contains(nameof(InvalidOperationException)));
    }

    [Fact(DisplayName = "A request its caller cancels rolls back at Debug: a caller that gave up is not a failure")]
    public async Task Caller_cancellation_rolls_back_at_debug()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<UnitOfWorkBehavior<TransactionalCommand, CommandResult>>();
        using var cts = new CancellationTokenSource();

        var act = () => harness.AsRequest(() => harness.Behavior(logger).Handle(new TransactionalCommand(), ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(CommandResult.FromSuccess());
        }, cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Log.Entries.Should().Equal("begin", "rollback");
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4207 && e.Level == LogLevel.Debug && e.Exception == null);
    }

    [Fact(DisplayName = "A commit failure is logged as the rollback it causes, at Information and without the exception it propagates")]
    public async Task Commit_failure_logs_the_rollback_without_the_exception()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: false);
        harness.UnitOfWork.FailNextCommit(new IOException("serialization failure"));
        var logger = new CapturingLogger<UnitOfWorkBehavior<TransactionalCommand, CommandResult>>();

        var act = () => harness.AsRequest(() => harness.Behavior(logger)
            .Handle(new TransactionalCommand(), _ => Succeed(harness), CancellationToken.None));

        await act.Should().ThrowAsync<IOException>();
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4204 && e.Level == LogLevel.Information && e.Exception == null && e.Message.Contains(nameof(IOException)));
    }

    [Fact(DisplayName = "A rollback that fails after a handler exception is logged at Error with its exception; the handler's exception surfaces")]
    public async Task Swallowed_rollback_failure_is_logged_as_an_error()
    {
        await using var harness = UnitOfWorkHarness.Create();
        harness.UnitOfWork.RollbackFailure = new IOException("rollback failed");
        var logger = new CapturingLogger<UnitOfWorkBehavior<TransactionalCommand, CommandResult>>();

        var act = () => harness.AsRequest(() => harness.Behavior(logger)
            .Handle(new TransactionalCommand(), _ => throw new InvalidOperationException("handler failed"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler failed");
        logger.Entries.Select(e => e.EventId.Id).Should().Equal(4201, 4205);
        logger.Entries.Single(e => e.EventId.Id == 4205).Should().Match<CapturedLogEntry>(e =>
            e.Level == LogLevel.Error && e.Exception is IOException);
    }

    [Fact(DisplayName = "A rollback failure that surfaces is not logged here: it is the request's failure")]
    public async Task Surfacing_rollback_failure_is_not_logged()
    {
        await using var harness = UnitOfWorkHarness.Create();
        harness.UnitOfWork.RollbackFailure = new IOException("rollback failed");
        var logger = new CapturingLogger<UnitOfWorkBehavior<IsolatedQuery, int>>();

        var act = () => harness.AsRequest(() => harness.Behavior(logger).Handle(new IsolatedQuery(), _ => Task.FromResult(1), CancellationToken.None));

        await act.Should().ThrowAsync<IOException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    public sealed class IsolatedQuery : QueryBase<int>, ITransactionalQuery
    {
        public IsolationLevel IsolationLevel { get; init; }
        public bool IsReadOnly => true;
    }

    public sealed class WritingQuery : QueryBase<int>, ITransactionalQuery
    {
        public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        public bool IsReadOnly => false;
    }

    public sealed class MintTokenCommand : ResultCommandBase<Guid>, ITransactionalCommand
    {
        public IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    }

    // The requests above are driven through the behavior directly; these handlers keep the test assembly's bindings
    // complete for the startup validator, which other tests run over the whole assembly.
    public sealed class IsolatedQueryHandler : IQueryHandler<IsolatedQuery, int>
    {
        public Task<int> Handle(IsolatedQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public sealed class WritingQueryHandler : IQueryHandler<WritingQuery, int>
    {
        public Task<int> Handle(WritingQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public sealed class MintTokenCommandHandler : IResultCommandHandler<MintTokenCommand, Guid>
    {
        public Task<CommandResult<Guid>> Handle(MintTokenCommand command, CancellationToken cancellationToken)
            => Task.FromResult(CommandResult<Guid>.FromSuccess(Guid.NewGuid()));
    }
}

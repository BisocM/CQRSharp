using CQRSharp.Core.Outbox;
using CQRSharp.Tests.Core;
using CQRSharp.Tests.Shared;
using FluentAssertions;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The unit-of-work behaviors act on the notifications their own request buffered, never on what an enclosing
///     request buffered before it, and a nested request that takes part in its caller's transaction is committed with it.
/// </summary>
public sealed class UnitOfWorkNestingTests
{
    [Fact(DisplayName = "A failed result discards the nested request's own notifications and leaves the enclosing request's alone")]
    public async Task Failed_result_discards_only_its_own_notifications()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var remaining = await harness.AsRequest(async () =>
        {
            var outer = OutboxOwner.Current!;
            harness.Outbox.TryBuffer(new OuterEvent()).Should().BeTrue();

            var result = await harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>().Handle(new TransactionalCommand(), _ =>
            {
                harness.Outbox.TryBuffer(new InnerEvent()).Should().BeTrue();
                return Task.FromResult(CommandResult.FromError("declined"));
            }, CancellationToken.None));

            result.IsSuccess.Should().BeFalse();
            return harness.Outbox.DrainOwned(outer);
        });

        remaining.Should().ContainSingle().Which.Should().BeOfType<OuterEvent>();
        harness.UnitOfWork.Rollbacks.Should().Be(1);
    }

    [Fact(DisplayName = "A handler exception discards the nested request's own notifications and leaves the enclosing request's alone")]
    public async Task Handler_exception_discards_only_its_own_notifications()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var remaining = await harness.AsRequest(async () =>
        {
            var outer = OutboxOwner.Current!;
            harness.Outbox.TryBuffer(new OuterEvent()).Should().BeTrue();

            var act = () => harness.AsRequest(() => harness.Behavior<TransactionalCommand, CommandResult>().Handle(new TransactionalCommand(), _ =>
            {
                harness.Outbox.TryBuffer(new InnerEvent()).Should().BeTrue();
                throw new InvalidOperationException("boom");
            }, CancellationToken.None));

            await act.Should().ThrowAsync<InvalidOperationException>();
            return harness.Outbox.DrainOwned(outer);
        });

        remaining.Should().ContainSingle().Which.Should().BeOfType<OuterEvent>();
        harness.UnitOfWork.Rollbacks.Should().Be(1);
    }

    [Fact(DisplayName = "A nested transactional request takes part in its caller's transaction; both requests' notifications commit with the caller")]
    public async Task Nested_request_commits_with_its_caller()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: true);
        var behavior = harness.Behavior<TransactionalCommand, CommandResult>();

        await harness.AsRequest(() => behavior.Handle(new TransactionalCommand(), async _ =>
        {
            harness.Outbox.TryBuffer(new OuterEvent()).Should().BeTrue();
            await harness.AsRequest(() => behavior.Handle(new TransactionalCommand(), _ =>
            {
                harness.Outbox.TryBuffer(new InnerEvent()).Should().BeTrue();
                return Task.FromResult(CommandResult.FromSuccess());
            }, CancellationToken.None));

            harness.Log.Entries.Should().Equal(new[] { "begin" }, "the nested request neither began nor committed a transaction of its own");
            return CommandResult.FromSuccess();
        }, CancellationToken.None));

        harness.Log.Entries.Should().Equal("begin", "store", "commit", "signal");
        harness.Store.Stored.Select(m => m.NotificationType).Should().BeEquivalentTo("ownership.outer", "ownership.inner");
    }
}

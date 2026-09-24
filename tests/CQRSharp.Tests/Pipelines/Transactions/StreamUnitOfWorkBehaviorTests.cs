using System.Data;
using System.Runtime.CompilerServices;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The streaming unit-of-work behavior shares the transaction lifecycle of the command/query one: a stream enumerated
///     to its end commits once, a faulted one rolls back and rethrows, one its consumer abandons rolls back, and a
///     read-only one rolls back instead of committing.
/// </summary>
public sealed class StreamUnitOfWorkBehaviorTests
{
    // The stream's handler publishes a durable notification as it starts, then yields the items.
    private static IAsyncEnumerable<int> Publishing(UnitOfWorkHarness harness, int[] items, CancellationToken ct)
    {
        harness.Publish();
        return Produce(items, ct);
    }

    [Fact(DisplayName = "A stream enumerated to its end commits once at its isolation level, with its notifications, and yields its items unchanged")]
    public async Task Full_enumeration_commits_once()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: true);

        var items = await harness.AsRequest(() => Drain(harness.StreamBehavior<TransactionalStream, int>()
            .Handle(new TransactionalStream { IsolationLevel = IsolationLevel.Serializable }, ct => Publishing(harness, [10, 20, 30], ct), CancellationToken.None)));

        items.Should().Equal(10, 20, 30);
        harness.Log.Entries.Should().Equal("begin", "store", "commit", "signal");
        harness.UnitOfWork.BeganWith.Should().Equal(IsolationLevel.Serializable);
    }

    [Fact(DisplayName = "With a transaction already active, the stream takes part in it without beginning, committing or rolling back")]
    public async Task Active_transaction_is_joined()
    {
        await using var harness = UnitOfWorkHarness.Create();
        harness.UnitOfWork.HasActiveTransaction = true;

        var items = await harness.AsRequest(() => Drain(harness.StreamBehavior<TransactionalStream, int>()
            .Handle(new TransactionalStream(), ct => Produce([1, 2], ct), CancellationToken.None)));

        items.Should().Equal(1, 2);
        harness.Log.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "A stream that throws mid-enumeration rolls back under no token, discards its notifications and rethrows")]
    public async Task Fault_rolls_back_and_rethrows()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var failure = new InvalidOperationException("stream failed mid-enumeration");
        var collected = new List<int>();

        var act = () => harness.AsRequest(async () =>
        {
            await foreach (var item in harness.StreamBehavior<TransactionalStream, int>().Handle(new TransactionalStream(), _ =>
                           {
                               harness.Publish();
                               return ThrowsAt([0, 1], failure);
                           }, CancellationToken.None))
                collected.Add(item);
            return 0;
        });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        collected.Should().Equal(0, 1);
        harness.Log.Entries.Should().Equal("begin", "rollback");
        harness.UnitOfWork.RollbackTokens.Should().Equal(CancellationToken.None);
        harness.Outbox.Count.Should().Be(0);
    }

    [Fact(DisplayName = "A stream its caller cancels rolls back at Debug, and nothing is logged as a failure")]
    public async Task Caller_cancellation_rolls_back_at_debug()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<StreamUnitOfWorkBehavior<TransactionalStream, int>>();
        using var cts = new CancellationTokenSource();

        var act = () => harness.AsRequest(async () =>
        {
            await foreach (var _ in harness.StreamBehavior(logger).Handle(new TransactionalStream(), ct => Produce([1, 2, 3], ct), cts.Token))
                await cts.CancelAsync();
            return 0;
        });

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Log.Entries.Should().Equal("begin", "rollback");
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e => e.EventId.Id == 4207 && e.Level == LogLevel.Debug);
    }

    [Fact(DisplayName = "A stream that faults and then fails to dispose rolls back and surfaces its own failure; the disposal failure is logged")]
    public async Task Disposal_failure_after_a_fault_keeps_the_fault()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<StreamUnitOfWorkBehavior<TransactionalStream, int>>();
        var failure = new InvalidOperationException("stream failed");
        var disposalFailure = new IOException("disposal failed");

        var act = () => harness.AsRequest(() => Drain(harness.StreamBehavior(logger)
            .Handle(new TransactionalStream(), _ => FailsOnDisposal([1], failure, disposalFailure), CancellationToken.None)));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        harness.Log.Entries.Should().Equal("begin", "rollback");
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4209 && e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(disposalFailure);
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4201, "the rollback is for the stream's failure");
    }

    [Fact(DisplayName = "A stream that runs to its end and then fails to dispose is rolled back as a failure, not as an abandoned stream, and surfaces the disposal failure")]
    public async Task Disposal_failure_after_the_end_is_a_failure()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<StreamUnitOfWorkBehavior<TransactionalStream, int>>();
        var disposalFailure = new IOException("disposal failed");

        var act = () => harness.AsRequest(() => Drain(harness.StreamBehavior(logger)
            .Handle(new TransactionalStream(), _ => FailsOnDisposal([1, 2], null, disposalFailure), CancellationToken.None)));

        (await act.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(disposalFailure);
        harness.Log.Entries.Should().Equal("begin", "rollback");
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4201);
        logger.Entries.Should().NotContain(e => e.EventId.Id == 4202 || e.EventId.Id == 4209);
    }

    [Fact(DisplayName = "A stream taking part in an active transaction that faults and then fails to dispose surfaces its own failure")]
    public async Task Participating_disposal_failure_keeps_the_fault()
    {
        await using var harness = UnitOfWorkHarness.Create();
        harness.UnitOfWork.HasActiveTransaction = true;
        var logger = new CapturingLogger<StreamUnitOfWorkBehavior<TransactionalStream, int>>();
        var failure = new InvalidOperationException("stream failed");

        var act = () => harness.AsRequest(() => Drain(harness.StreamBehavior(logger)
            .Handle(new TransactionalStream(), _ => FailsOnDisposal([1], failure, new IOException("disposal failed")), CancellationToken.None)));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        harness.Log.Entries.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4209);
    }

    [Fact(DisplayName = "A commit that fails rolls back and surfaces the failure to the consumer")]
    public async Task Failed_commit_rolls_back()
    {
        await using var harness = UnitOfWorkHarness.Create(storeJoinsUnitOfWork: false);
        harness.UnitOfWork.FailNextCommit(new IOException("commit failed"));

        var act = () => harness.AsRequest(() => Drain(harness.StreamBehavior<TransactionalStream, int>()
            .Handle(new TransactionalStream(), ct => Publishing(harness, [1], ct), CancellationToken.None)));

        (await act.Should().ThrowAsync<IOException>()).WithMessage("commit failed");
        harness.Log.Entries.Should().Equal("begin", "commit-failed", "rollback");
        harness.Store.Stored.Should().BeEmpty();
    }

    [Fact(DisplayName = "A read-only transactional stream is rolled back, not committed")]
    public async Task Read_only_stream_rolls_back()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var items = await harness.AsRequest(() => Drain(harness.StreamBehavior<ReadOnlyStream, int>()
            .Handle(new ReadOnlyStream(), ct => Produce([1, 2], ct), CancellationToken.None)));

        items.Should().Equal(1, 2);
        harness.Log.Entries.Should().Equal("begin", "rollback");
    }

    [Fact(DisplayName = "A consumer that stops early rolls the transaction back exactly once and never commits")]
    public async Task Early_disposal_rolls_back_once()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var items = await harness.AsRequest(async () =>
        {
            var taken = new List<int>();
            await foreach (var item in harness.StreamBehavior<TransactionalStream, int>()
                               .Handle(new TransactionalStream(), ct => Publishing(harness, [1, 2, 3], ct), CancellationToken.None))
            {
                taken.Add(item);
                break;
            }

            return taken;
        });

        items.Should().Equal(1);
        harness.Log.Entries.Should().Equal("begin", "rollback");
        harness.UnitOfWork.Commits.Should().Be(0);
        harness.Outbox.Count.Should().Be(0, "the abandoned stream's notification is discarded with its work");
    }

    [Fact(DisplayName = "A consumer that stops a stream that writes early is logged as the rollback it causes, at Information")]
    public async Task Abandoned_writing_stream_logs_its_rollback_at_information()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<StreamUnitOfWorkBehavior<TransactionalStream, int>>();

        await harness.AsRequest(() => TakeFirst(harness.StreamBehavior(logger).Handle(new TransactionalStream(), ct => Produce([1, 2, 3], ct), CancellationToken.None)));

        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4202 && e.Level == LogLevel.Information && e.Exception == null);
    }

    [Fact(DisplayName = "A consumer that stops a read-only stream early loses nothing, so its rollback is logged at Debug only")]
    public async Task Abandoned_read_only_stream_logs_its_rollback_at_debug()
    {
        await using var harness = UnitOfWorkHarness.Create();
        var logger = new CapturingLogger<StreamUnitOfWorkBehavior<ReadOnlyStream, int>>();

        await harness.AsRequest(() => TakeFirst(harness.StreamBehavior(logger).Handle(new ReadOnlyStream(), ct => Produce([1, 2, 3], ct), CancellationToken.None)));

        harness.Log.Entries.Should().Equal("begin", "rollback");
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4208 && e.Level == LogLevel.Debug && e.Exception == null);
    }

    [Fact(DisplayName = "A stream whose enumerator fails on disposal is rolled back, not committed")]
    public async Task Disposal_failure_rolls_back()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var act = () => harness.AsRequest(() => Drain(harness.StreamBehavior<TransactionalStream, int>()
            .Handle(new TransactionalStream(), _ => new ThrowOnDispose(), CancellationToken.None)));

        (await act.Should().ThrowAsync<IOException>()).WithMessage("dispose failed");
        harness.UnitOfWork.Rollbacks.Should().Be(1);
        harness.UnitOfWork.Commits.Should().Be(0);
    }

    [Fact(DisplayName = "A stream that is not transactional passes straight through")]
    public async Task Non_transactional_stream_bypasses_the_unit_of_work()
    {
        await using var harness = UnitOfWorkHarness.Create();

        var items = await Drain(harness.StreamBehavior<PlainStream, int>().Handle(new PlainStream(), ct => Produce([1, 2], ct), CancellationToken.None));

        items.Should().Equal(1, 2);
        harness.Log.Entries.Should().BeEmpty();
    }

    private static async Task<int> TakeFirst(IAsyncEnumerable<int> stream)
    {
        await foreach (var item in stream)
            return item;

        throw new InvalidOperationException("The stream was empty.");
    }

    // A stream that writes: committed at its end like a command.
    public sealed class TransactionalStream : StreamRequestBase<int>, ITransactionalQuery
    {
        public IsolationLevel IsolationLevel { get; init; }
        public bool IsReadOnly => false;
    }

    public sealed class ReadOnlyStream : StreamRequestBase<int>, ITransactionalQuery
    {
        public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        public bool IsReadOnly => true;
    }

    public sealed class PlainStream : StreamRequestBase<int>;

    // Driven through the behavior directly; the handlers keep the test assembly's bindings complete.
    public sealed class TransactionalStreamHandler : IStreamRequestHandler<TransactionalStream, int>
    {
        public async IAsyncEnumerable<int> Handle(TransactionalStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    public sealed class ReadOnlyStreamHandler : IStreamRequestHandler<ReadOnlyStream, int>
    {
        public async IAsyncEnumerable<int> Handle(ReadOnlyStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    public sealed class PlainStreamHandler : IStreamRequestHandler<PlainStream, int>
    {
        public async IAsyncEnumerable<int> Handle(PlainStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowOnDispose : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator();

        private sealed class Enumerator : IAsyncEnumerator<int>
        {
            private bool _yielded;
            public int Current => 1;

            public ValueTask<bool> MoveNextAsync()
            {
                if (_yielded) return ValueTask.FromResult(false);
                _yielded = true;
                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync() => throw new IOException("dispose failed");
        }
    }
}

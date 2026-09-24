using System.Text.Json;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Core.OutboxTestHarness;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The processor's claim on a message from the claim to the outcome: the lease renewed before a message is
///     dispatched, a claim lost before or after dispatch, a shutdown that hands every unfinished claim back — the renewed
///     one included — and a handler failure that only looks like a shutdown or a corrupt payload. Runs the real processor
///     over a <see cref="ScriptedOutboxStore" /> on a <see cref="FakeTimeProvider" /> that moves only when a test moves
///     it; a drain (<see cref="OutboxTestHarness.DrainAsync" />) is how a test knows the processor finished with what it
///     claimed.
/// </summary>
public sealed class OutboxProcessorLeaseTests
{
    private static readonly TimeSpan Lease = new InMemoryOutboxStoreOptions().VisibilityTimeout;

    // Past half the lease between the claim and the first dispatch, so the first message is renewed before it runs.
    private static void AgeTheFirstClaim(ScriptedOutboxStore store, FakeTimeProvider time)
        => store.AfterClaim = _ =>
        {
            store.AfterClaim = null;
            time.Advance(Lease * 0.6);
        };

    [Fact(DisplayName = "Outbox: a shutdown hands back the in-flight claim as renewed, and the claims of the batch's undispatched messages without starting them")]
    public async Task A_shutdown_releases_the_renewed_claim_and_the_undispatched_ones()
    {
        ScriptedOutboxStore? store = null;
        var (provider, time, probe) = BuildProbed(
            p => p.MaxDegreeOfParallelism = 1,
            outbox: clock => store = new ScriptedOutboxStore(clock) { RotateTokenOnRenewal = true });
        await using var _ = provider;
        probe.HoldEachDelivery = true;
        await PublishAsync(provider, new ParallelProbe(1, null), new ParallelProbe(2, null), new ParallelProbe(3, null));
        AgeTheFirstClaim(store!, time);

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // The first message runs, held on the stopping token; the other two wait for the one delivery slot.
            await probe.EnteredAsync(1);
        }
        finally
        {
            // The interrupted delivery frees its slot while the shutdown is being signalled, typically before the
            // batch loop's own wait sees the cancellation: that slot must not start the next message.
            await processor.StopAsync(CancellationToken.None);
        }

        var batch = store!.Claimed.Should().ContainSingle().Which;
        batch.Should().HaveCount(3);
        var released = store.Released;
        released.Should().HaveCount(3);
        released.Should().ContainSingle(c => c.MessageId == batch[0].Message.Id)
            .Which.Token.Should().StartWith("rotated-", "a store that rotates the token on renewal only recognises the renewed claim");
        released.Where(c => c.MessageId != batch[0].Message.Id).Should().BeEquivalentTo(new[] { batch[1].Claim, batch[2].Claim });
        (await store.Inner.ClaimPendingAsync(10, TestContext.Current.CancellationToken)).Should().HaveCount(3,
            "every handed-back message is claimable at once, without waiting out its lease");
        probe.Attempts.Should().Equal([1], "the undispatched messages never reached their handler");
    }

    [Fact(DisplayName = "Outbox: a claim lost before dispatch skips the message without running its handler or recording an outcome")]
    public async Task A_claim_lost_before_dispatch_is_skipped()
    {
        ScriptedOutboxStore? store = null;
        var (provider, time, probe) = BuildProbed(outbox: clock => store = new ScriptedOutboxStore(clock) { LoseClaimOnRenewal = true });
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        AgeTheFirstClaim(store!, time);

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        store!.Calls.Should().Contain("renew");
        store.Calls.Should().NotContain(new[] { "processed", "increment", "failed", "defer" }, "another processor owns the message and reports its outcome");
        probe.Attempts.Should().BeEmpty();
    }

    [Fact(DisplayName = "Outbox: a renewal that fails charges no attempt, even the last one: the message waits out its lease and is delivered on a later claim")]
    public async Task A_failed_renewal_charges_no_attempt()
    {
        ScriptedOutboxStore? store = null;
        var (provider, time, probe) = BuildProbed(p => p.MaxAttempts = 1,
            outbox: clock => store = new ScriptedOutboxStore(clock) { RenewalFailure = new IOException("store unreachable") });
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));
        AgeTheFirstClaim(store!, time);

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
            probe.Attempts.Should().BeEmpty("the handler never ran");
            store!.Calls.Should().NotContain(new[] { "increment", "failed" }, "a failed renewal is not a failed attempt");

            time.Advance(Lease);
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1, "the lease ran out and the message was delivered, on its first attempt");
        (await store!.Inner.GetDeadLettersAsync(10, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact(DisplayName = "Outbox: a claim lost after dispatch leaves the new owner's outcome alone: no retry and no dead letter")]
    public async Task A_claim_lost_after_dispatch_records_nothing_more()
    {
        ScriptedOutboxStore? store = null;
        var (provider, _, probe) = BuildProbed(outbox: clock => store = new ScriptedOutboxStore(clock) { LoseClaimOnProcessedMark = true });
        await using var _ = provider;
        await PublishAsync(provider, new ParallelProbe(1, null));

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        probe.Completed.Should().HaveCount(1);
        store!.Calls.Should().Contain("processed");
        store.Calls.Should().NotContain(new[] { "increment", "failed" });
    }

    [Fact(DisplayName = "Outbox: a handler's own OperationCanceledException while the host runs is a failed attempt, and the processor keeps polling")]
    public Task A_handlers_own_cancellation_is_a_failed_attempt()
        => AssertFailedAttemptAsync(new OperationCanceledException("the handler's own timeout"));

    [Fact(DisplayName = "Outbox: a JsonException thrown by the handler is a failed attempt, not a corrupt payload")]
    public Task A_handlers_json_exception_is_a_failed_attempt()
        => AssertFailedAttemptAsync(new JsonException("the handler could not parse its own input"));

    private static async Task AssertFailedAttemptAsync(Exception failure)
    {
        ScriptedOutboxStore? store = null;
        var (provider, _, probe) = BuildProbed(outbox: clock => store = new ScriptedOutboxStore(clock));
        await using var _ = provider;
        probe.Failure = failure;
        await PublishAsync(provider, new ParallelProbe(1, null));

        var processor = Processor(provider);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // The poll after the failure: the processor went on rather than unwinding.
            await DrainAsync(provider);
            processor.ExecuteTask!.IsCompleted.Should().BeFalse("the handler's exception must not stop the processor");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        store!.Calls.Should().Contain("increment");
        store.Calls.Should().NotContain("failed", "only the payload is judged corrupt, and this attempt was not the last");
        store.Inner.Snapshot().Should().ContainSingle().Which.Should().Match<OutboxMessage>(m =>
            m.Status == OutboxMessageStatus.Pending && m.AttemptCount == 1 && m.LastError!.Contains(failure.GetType().Name));
    }
}

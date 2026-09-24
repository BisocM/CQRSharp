using CQRSharp.Core.Idempotency;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Runs the shared <see cref="IdempotencyStoreContractTests" /> against the in-process InMemoryIdempotencyStore, plus
///     an in-memory-specific test of the sweep that keeps the store from growing without bound.
/// </summary>
public sealed class InMemoryIdempotencyStoreContractTests : IdempotencyStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override TimeSpan Retention => TimeSpan.FromMinutes(30);

    protected override Task<IIdempotencyStore> CreateStoreAsync() => Task.FromResult<IIdempotencyStore>(NewStore());

    private InMemoryIdempotencyStore NewStore()
        => new(Time, Options.Create(new InMemoryIdempotencyStoreOptions { Retention = Retention }));

    [Fact(DisplayName = "The in-memory idempotency sweep drops the keys whose window has passed and keeps the ones still inside it")]
    public async Task The_sweep_removes_expired_keys_and_keeps_live_ones()
    {
        var store = NewStore();
        var ct = TestContext.Current.CancellationToken;

        // The first claim also sets the next sweep to one retention window from now.
        (await store.TryClaimAsync("expired", null, ct)).IsClaimed.Should().BeTrue();
        Time.Advance(Retention / 2);
        var live = await store.TryClaimAsync("live", null, ct);
        await store.CompleteAsync("live", live.Token!, [1], ct);

        // Past the first key's window, not the second's; the next claim runs the sweep.
        Time.Advance(Retention / 2 + TimeSpan.FromSeconds(1));
        (await store.TryClaimAsync("trigger", null, ct)).IsClaimed.Should().BeTrue();

        store.Count.Should().Be(2, "the sweep removed the expired key, and kept the live one beside the new one");
        (await store.TryClaimAsync("live", null, ct)).Status.Should().Be(IdempotencyClaimStatus.Completed,
            "a key still inside its window survives the sweep with its result");
        (await store.TryClaimAsync("expired", null, ct)).IsClaimed.Should().BeTrue("the expired key is free again");
    }
}

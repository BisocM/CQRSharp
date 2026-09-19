using CQRSharp.Pipelines;
using FluentAssertions;
using Xunit;

namespace CQRSharp.Testing.Idempotency;

/// <summary>
///     The single, reusable conformance suite for every <see cref="IIdempotencyStore" /> implementation. Each store
///     (in-memory, Redis, EF Core, ...) derives from this class and supplies a fresh store, so every store proves the
///     same atomic claim, duplicate-rejection, release, and concurrency semantics with no duplicated assertions. Each
///     test uses a unique key so a shared backing service (e.g. one Redis instance) cannot bleed state between tests.
/// </summary>
public abstract class IdempotencyStoreContractTests
{
    /// <summary>Creates a store bound to a fresh, empty key space (or one that unique keys keep isolated).</summary>
    protected abstract Task<IIdempotencyStore> CreateStoreAsync();

    private static string NewKey() => $"contract:{Guid.NewGuid():N}";

    [SkippableFact]
    public async Task TryClaim_on_a_fresh_key_succeeds()
    {
        var store = await CreateStoreAsync();

        (await store.TryClaimAsync(NewKey(), CancellationToken.None)).IsClaimed.Should().BeTrue();
    }

    [SkippableFact]
    public async Task TryClaim_on_an_already_claimed_key_is_rejected_as_a_duplicate()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed.Should().BeTrue();
        (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed.Should().BeFalse("the key is already claimed");
    }

    [SkippableFact]
    public async Task Releasing_a_claim_makes_the_key_claimable_again()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed.Should().BeTrue();
        await store.ReleaseAsync(key, CancellationToken.None);
        (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed.Should().BeTrue("a released key is free to be claimed again");
    }

    [SkippableFact]
    public async Task Releasing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();

        var act = () => store.ReleaseAsync(NewKey(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task A_claimed_but_unfinished_key_reports_in_progress()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, CancellationToken.None);

        var duplicate = await store.TryClaimAsync(key, CancellationToken.None);

        duplicate.Status.Should().Be(IdempotencyClaimStatus.InProgress);
        duplicate.StoredResult.Should().BeNull();
    }

    [SkippableFact]
    public async Task A_completed_key_reports_completed_and_hands_back_the_stored_result()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        byte[] result = [0, 1, 2, 255, 58, 99]; // arbitrary bytes, including the ':' and 'c' a naive encoding could trip on
        await store.TryClaimAsync(key, CancellationToken.None);

        await store.CompleteAsync(key, result, CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, CancellationToken.None);

        duplicate.Status.Should().Be(IdempotencyClaimStatus.Completed);
        duplicate.StoredResult.Should().Equal(result);
    }

    [SkippableFact]
    public async Task A_key_completed_without_a_result_reports_completed_with_none()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, CancellationToken.None);

        await store.CompleteAsync(key, null, CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, CancellationToken.None);

        duplicate.Status.Should().Be(IdempotencyClaimStatus.Completed);
        duplicate.StoredResult.Should().BeNull();
    }

    [SkippableFact]
    public async Task A_completed_key_cannot_be_released()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, CancellationToken.None);
        await store.CompleteAsync(key, [7], CancellationToken.None);

        // A late release (a failure path racing the completion) must not forget a request that went through.
        await store.ReleaseAsync(key, CancellationToken.None);

        (await store.TryClaimAsync(key, CancellationToken.None)).Status.Should().Be(IdempotencyClaimStatus.Completed);
    }

    [SkippableFact]
    public async Task Completing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var act = () => store.CompleteAsync(key, [1], CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed.Should().BeTrue("completing a key nobody claimed must not create one");
    }

    [SkippableFact]
    public async Task Concurrent_claims_of_the_same_key_yield_exactly_one_winner()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.TryClaimAsync(key, CancellationToken.None))));

        results.Count(claim => claim.IsClaimed).Should().Be(1, "exactly one concurrent caller may claim a given key");
    }
}

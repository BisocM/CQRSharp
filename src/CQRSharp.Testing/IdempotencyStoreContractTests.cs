using CQRSharp.Pipelines;
using Xunit;

namespace CQRSharp.Testing;

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

    /// <summary>Contract: claiming a key nobody holds succeeds.</summary>
    [SkippableFact]
    public async Task TryClaim_on_a_fresh_key_succeeds()
    {
        var store = await CreateStoreAsync();

        Assert.True(
            (await store.TryClaimAsync(NewKey(), CancellationToken.None)).IsClaimed,
            "TryClaimAsync must claim a key nobody holds.");
    }

    /// <summary>Contract: a second claim of a held key is rejected as a duplicate.</summary>
    [SkippableFact]
    public async Task TryClaim_on_an_already_claimed_key_is_rejected_as_a_duplicate()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        Assert.True(
            (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed,
            "TryClaimAsync must claim a key nobody holds.");
        Assert.False(
            (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed,
            "TryClaimAsync must reject a key that is already claimed.");
    }

    /// <summary>Contract: releasing an unfinished claim frees the key to be claimed again (the failed request may be retried).</summary>
    [SkippableFact]
    public async Task Releasing_a_claim_makes_the_key_claimable_again()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        Assert.True(
            (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed,
            "TryClaimAsync must claim a key nobody holds.");
        await store.ReleaseAsync(key, CancellationToken.None);
        Assert.True(
            (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed,
            "A released key must be free to be claimed again.");
    }

    /// <summary>Contract: releasing a key the store does not know is a no-op and does not throw.</summary>
    [SkippableFact]
    public async Task Releasing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => store.ReleaseAsync(NewKey(), CancellationToken.None));

        Assert.True(exception is null, $"ReleaseAsync on an unknown key must be a no-op, but it threw: {exception}");
    }

    /// <summary>Contract: a duplicate of a claimed-but-unfinished key reports <c>InProgress</c> with no stored result.</summary>
    [SkippableFact]
    public async Task A_claimed_but_unfinished_key_reports_in_progress()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, CancellationToken.None);

        var duplicate = await store.TryClaimAsync(key, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, duplicate.Status, "Status of a duplicate of an unfinished claim");
        Assert.True(duplicate.StoredResult is null, "An in-progress duplicate must not carry a stored result.");
    }

    /// <summary>Contract: a duplicate of a completed key reports <c>Completed</c> and returns the stored result byte-for-byte.</summary>
    [SkippableFact]
    public async Task A_completed_key_reports_completed_and_hands_back_the_stored_result()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        byte[] result = [0, 1, 2, 255, 58, 99]; // arbitrary bytes, including the ':' and 'c' a naive encoding could trip on
        await store.TryClaimAsync(key, CancellationToken.None);

        await store.CompleteAsync(key, result, CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.Completed, duplicate.Status, "Status of a duplicate of a completed key");
        Assert.True(duplicate.StoredResult is not null, "A completed duplicate must hand back the stored result.");
        ContractAssert.SequenceEqual(result, duplicate.StoredResult!, "The stored result must round-trip byte-for-byte");
    }

    /// <summary>Contract: a key completed with a <see langword="null" /> result reports <c>Completed</c> with no stored result.</summary>
    [SkippableFact]
    public async Task A_key_completed_without_a_result_reports_completed_with_none()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, CancellationToken.None);

        await store.CompleteAsync(key, null, CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.Completed, duplicate.Status, "Status of a duplicate of a completed key");
        Assert.True(duplicate.StoredResult is null, "A key completed without a result must report no stored result.");
    }

    /// <summary>Contract: a release that arrives after completion must not forget the completed request.</summary>
    [SkippableFact]
    public async Task A_completed_key_cannot_be_released()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, CancellationToken.None);
        await store.CompleteAsync(key, [7], CancellationToken.None);

        // A late release (a failure path racing the completion) must not forget a request that went through.
        await store.ReleaseAsync(key, CancellationToken.None);

        ContractAssert.Equal(
            IdempotencyClaimStatus.Completed,
            (await store.TryClaimAsync(key, CancellationToken.None)).Status,
            "Status after releasing a completed key (a release must not forget a completed request)");
    }

    /// <summary>Contract: completing a key nobody claimed is a no-op: it does not throw and does not create the key.</summary>
    [SkippableFact]
    public async Task Completing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var exception = await Record.ExceptionAsync(() => store.CompleteAsync(key, [1], CancellationToken.None));

        Assert.True(exception is null, $"CompleteAsync on an unknown key must be a no-op, but it threw: {exception}");
        Assert.True(
            (await store.TryClaimAsync(key, CancellationToken.None)).IsClaimed,
            "Completing a key nobody claimed must not create one.");
    }

    /// <summary>Contract: claiming is atomic; of many concurrent claims of one key exactly one wins.</summary>
    [SkippableFact]
    public async Task Concurrent_claims_of_the_same_key_yield_exactly_one_winner()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.TryClaimAsync(key, CancellationToken.None))));

        ContractAssert.Equal(
            1,
            results.Count(claim => claim.IsClaimed),
            "Number of winners among concurrent claims of one key (exactly one caller may claim it)");
    }
}

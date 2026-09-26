using CQRSharp.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CQRSharp.Testing;

/// <summary>
///     The single, reusable conformance suite for every <see cref="IIdempotencyStore" /> implementation. Each store
///     (in-memory, Redis, EF Core, ...) derives from this class and supplies a fresh store bound to <see cref="Time" />,
///     so every store proves the same atomic claim, duplicate-rejection, payload-fingerprint, release, concurrency and
///     retention semantics with no duplicated assertions. Each test uses a unique key so a shared backing service (e.g.
///     one Redis instance) cannot bleed state between tests.
/// </summary>
public abstract class IdempotencyStoreContractTests : IAsyncLifetime
{
    /// <summary>Runs before each test; nothing by default.</summary>
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     Runs after each test. Override it to dispose whatever <c>CreateStoreAsync</c> opened (a connection, a
    ///     context) so a long suite does not leak one per test.
    /// </summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     The clock the store-under-test reads; the retention tests advance it. Unused by a store whose backing service
    ///     keeps its own time (see <see cref="SupportsClockDrivenRetention" />).
    /// </summary>
    protected abstract FakeTimeProvider Time { get; }

    /// <summary>
    ///     Creates a store bound to a fresh, empty key space (or one that unique keys keep isolated), reading time from
    ///     <see cref="Time" /> and configured with <see cref="Retention" />.
    /// </summary>
    protected abstract Task<IIdempotencyStore> CreateStoreAsync();

    /// <summary>The retention window the store-under-test is configured with; the retention tests advance the clock around it.</summary>
    protected virtual TimeSpan Retention => TimeSpan.FromHours(24);

    /// <summary>
    ///     Whether advancing <see cref="Time" /> ages claims out. A store whose expiry is timed by its backing service
    ///     (Redis) returns <c>false</c>, and the retention tests are skipped for it; it should prove the same guarantees
    ///     against its service's clock in tests of its own.
    /// </summary>
    protected virtual bool SupportsClockDrivenRetention => true;

    private static string NewKey() => $"contract:{Guid.NewGuid():N}";

    /// <summary>Contract: claiming a key nobody holds succeeds.</summary>
    [Fact]
    public async Task TryClaim_on_a_fresh_key_succeeds()
    {
        var store = await CreateStoreAsync();

        Assert.True(
            (await store.TryClaimAsync(NewKey(), null, CancellationToken.None)).IsClaimed,
            "TryClaimAsync must claim a key nobody holds.");
    }

    /// <summary>Contract: a second claim of a held key is rejected as a duplicate.</summary>
    [Fact]
    public async Task TryClaim_on_an_already_claimed_key_is_rejected_as_a_duplicate()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        Assert.True(
            (await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed,
            "TryClaimAsync must claim a key nobody holds.");
        Assert.False(
            (await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed,
            "TryClaimAsync must reject a key that is already claimed.");
    }

    /// <summary>Contract: releasing an unfinished claim frees the key to be claimed again (the failed request may be retried).</summary>
    [Fact]
    public async Task Releasing_a_claim_makes_the_key_claimable_again()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);
        Assert.True(claim.IsClaimed, "TryClaimAsync must claim a key nobody holds.");
        Assert.False(string.IsNullOrEmpty(claim.Token), "A claim must carry a token (IdempotencyClaim.ClaimedWith).");
        await store.ReleaseAsync(key, claim.Token!, CancellationToken.None);
        Assert.True(
            (await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed,
            "A released key must be free to be claimed again.");
    }

    /// <summary>Contract: releasing a key the store does not know is a no-op and does not throw.</summary>
    [Fact]
    public async Task Releasing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();

        var exception = await Record.ExceptionAsync(() => store.ReleaseAsync(NewKey(), "not-a-claim", CancellationToken.None));

        Assert.True(exception is null, $"ReleaseAsync on an unknown key must be a no-op, but it threw: {exception}");
    }

    /// <summary>Contract: complete and release act only on the claim their token names; another token changes nothing.</summary>
    [Fact]
    public async Task Another_claimants_token_neither_completes_nor_releases_the_claim()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);
        Assert.True(claim.IsClaimed, "TryClaimAsync must claim a key nobody holds.");

        await store.ReleaseAsync(key, "someone-else", CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status after a release with another claimant's token (the claim must still be held)");

        await store.CompleteAsync(key, "someone-else", [9], CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status after a completion with another claimant's token (the claim must still be in flight)");
    }

    /// <summary>Contract: a duplicate of a claimed-but-unfinished key reports <c>InProgress</c> with no stored result.</summary>
    [Fact]
    public async Task A_claimed_but_unfinished_key_reports_in_progress()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        await store.TryClaimAsync(key, null, CancellationToken.None);

        var duplicate = await store.TryClaimAsync(key, null, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, duplicate.Status, "Status of a duplicate of an unfinished claim");
        Assert.True(duplicate.StoredResult is null, "An in-progress duplicate must not carry a stored result.");
    }

    /// <summary>Contract: a duplicate of a completed key reports <c>Completed</c> and returns the stored result byte-for-byte.</summary>
    [Fact]
    public async Task A_completed_key_reports_completed_and_hands_back_the_stored_result()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        byte[] result = [0, 1, 2, 255, 58, 99]; // arbitrary bytes, including the ':' and 'c' a naive encoding could trip on
        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);

        await store.CompleteAsync(key, claim.Token!, result, CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, null, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.Completed, duplicate.Status, "Status of a duplicate of a completed key");
        Assert.True(duplicate.StoredResult is not null, "A completed duplicate must hand back the stored result.");
        ContractAssert.SequenceEqual(result, duplicate.StoredResult!, "The stored result must round-trip byte-for-byte");
    }

    /// <summary>Contract: a key completed with a <see langword="null" /> result reports <c>Completed</c> with no stored result.</summary>
    [Fact]
    public async Task A_key_completed_without_a_result_reports_completed_with_none()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);

        await store.CompleteAsync(key, claim.Token!, null, CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, null, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.Completed, duplicate.Status, "Status of a duplicate of a completed key");
        Assert.True(duplicate.StoredResult is null, "A key completed without a result must report no stored result.");
    }

    /// <summary>
    ///     Contract: a key completed with an empty result reports <c>Completed</c> and hands back that empty result, not
    ///     "no result": a result serializer may legitimately produce zero bytes, and its duplicate must still be replayed.
    /// </summary>
    [Fact]
    public async Task A_key_completed_with_an_empty_result_hands_back_an_empty_result()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);

        await store.CompleteAsync(key, claim.Token!, [], CancellationToken.None);
        var duplicate = await store.TryClaimAsync(key, null, CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.Completed, duplicate.Status, "Status of a duplicate of a completed key");
        Assert.True(duplicate.StoredResult is not null, "A key completed with an empty result must hand back that result, not report none.");
        ContractAssert.Equal(0, duplicate.StoredResult!.Length, "Length of the stored result of a key completed with an empty one");
    }

    /// <summary>Contract: a release that arrives after completion must not forget the completed request.</summary>
    [Fact]
    public async Task A_completed_key_cannot_be_released()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);
        await store.CompleteAsync(key, claim.Token!, [7], CancellationToken.None);

        // A late release (a failure path racing the completion) must not forget a request that went through.
        await store.ReleaseAsync(key, claim.Token!, CancellationToken.None);

        ContractAssert.Equal(
            IdempotencyClaimStatus.Completed,
            (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status after releasing a completed key (a release must not forget a completed request)");
    }

    /// <summary>Contract: completing a key nobody claimed is a no-op: it does not throw and does not create the key.</summary>
    [Fact]
    public async Task Completing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var exception = await Record.ExceptionAsync(() => store.CompleteAsync(key, "not-a-claim", [1], CancellationToken.None));

        Assert.True(exception is null, $"CompleteAsync on an unknown key must be a no-op, but it threw: {exception}");
        Assert.True(
            (await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed,
            "Completing a key nobody claimed must not create one.");
    }

    /// <summary>Contract: claiming is atomic; of many concurrent claims of one key exactly one wins.</summary>
    [Fact]
    public async Task Concurrent_claims_of_the_same_key_yield_exactly_one_winner()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.TryClaimAsync(key, null, CancellationToken.None))));

        ContractAssert.Equal(
            1,
            results.Count(claim => claim.IsClaimed),
            "Number of winners among concurrent claims of one key (exactly one caller may claim it)");
    }

    // ---- Retention ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Contract: a claim nobody completes or releases (its claimant crashed) holds the key for the retention window
    ///     and no longer: just before it ends a duplicate is still in progress, just after it the key can be claimed again.
    /// </summary>
    [Fact]
    public async Task An_abandoned_claim_expires_after_the_retention_window()
    {
        Assert.SkipUnless(SupportsClockDrivenRetention, "This store's claims expire on its backing service's clock.");
        var store = await CreateStoreAsync();
        var key = NewKey();
        Assert.True((await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed, "TryClaimAsync must claim a key nobody holds.");

        Time.Advance(Retention - TimeSpan.FromSeconds(1));
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status of a duplicate just before the retention window ends");

        Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed,
            "Once the retention window has passed, an abandoned claim must no longer hold the key.");
    }

    /// <summary>
    ///     Contract: a key taken over after its claim expired carries a new token, and the stale claimant's token neither
    ///     releases nor completes the successor's claim; the successor's own token still does.
    /// </summary>
    [Fact]
    public async Task A_stale_claimant_can_neither_release_nor_complete_its_successors_claim()
    {
        Assert.SkipUnless(SupportsClockDrivenRetention, "This store's claims expire on its backing service's clock.");
        var store = await CreateStoreAsync();
        var key = NewKey();
        var stale = await store.TryClaimAsync(key, null, CancellationToken.None);
        Assert.True(stale.IsClaimed, "TryClaimAsync must claim a key nobody holds.");

        Time.Advance(Retention + TimeSpan.FromSeconds(1));
        var successor = await store.TryClaimAsync(key, null, CancellationToken.None);
        Assert.True(successor.IsClaimed, "Once the retention window has passed, the key must be claimable again.");
        ContractAssert.NotEqual(stale.Token, successor.Token, "The token of a claim that took over an expired one (every claim gets its own)");

        await store.ReleaseAsync(key, stale.Token!, CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status after the stale claimant released (the successor's claim must still be held)");

        await store.CompleteAsync(key, stale.Token!, [9], CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status after the stale claimant completed (the successor's claim must still be in flight)");

        await store.CompleteAsync(key, successor.Token!, [1], CancellationToken.None);
        var replay = await store.TryClaimAsync(key, null, CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.Completed, replay.Status, "Status after the successor completed with its own token");
        ContractAssert.SequenceEqual(new byte[] { 1 }, replay.StoredResult!, "The successor's result, not the stale claimant's");
    }

    /// <summary>
    ///     Contract: completing a key keeps the expiry its claim was given: the retention window runs from the claim, not
    ///     from the completion, so a key completed late in its window is forgotten when the window ends.
    /// </summary>
    [Fact]
    public async Task Completing_a_key_keeps_the_expiry_of_its_claim()
    {
        Assert.SkipUnless(SupportsClockDrivenRetention, "This store's claims expire on its backing service's clock.");
        var store = await CreateStoreAsync();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, null, CancellationToken.None);
        Assert.True(claim.IsClaimed, "TryClaimAsync must claim a key nobody holds.");

        Time.Advance(Retention - TimeSpan.FromSeconds(1));
        await store.CompleteAsync(key, claim.Token!, [7], CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.Completed, (await store.TryClaimAsync(key, null, CancellationToken.None)).Status,
            "Status of a duplicate right after the completion");

        Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed,
            "A completed key must be forgotten when its claim's retention window ends, not a window after the completion.");
    }

    // ---- Payload fingerprints ------------------------------------------------------------------------------------

    /// <summary>Contract: keys are compared ordinally and case-sensitively; a key differing only in case is another key.</summary>
    [Fact]
    public async Task Keys_are_case_sensitive()
    {
        var store = await CreateStoreAsync();
        var key = NewKey() + "-abc";

        Assert.True((await store.TryClaimAsync(key, null, CancellationToken.None)).IsClaimed, "TryClaimAsync must claim a key nobody holds.");
        Assert.True((await store.TryClaimAsync(key.ToUpperInvariant(), null, CancellationToken.None)).IsClaimed,
            "A key that differs only in case is a different key (ordinal, case-sensitive comparison).");
    }

    /// <summary>Contract: a key reused with a different fingerprint reports a payload mismatch, whether the original is still running or completed; the same fingerprint behaves as before.</summary>
    [Fact]
    public async Task A_key_reused_with_a_different_fingerprint_reports_a_payload_mismatch()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var claim = await store.TryClaimAsync(key, "fp-a", CancellationToken.None);
        Assert.True(claim.IsClaimed, "TryClaimAsync must claim a key nobody holds.");

        ContractAssert.Equal(IdempotencyClaimStatus.PayloadMismatch, (await store.TryClaimAsync(key, "fp-b", CancellationToken.None)).Status,
            "Status of a claim that reuses a held key with a different fingerprint");
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(key, "fp-a", CancellationToken.None)).Status,
            "Status of a claim that reuses a held key with the same fingerprint");

        await store.CompleteAsync(key, claim.Token!, [1, 2, 3], CancellationToken.None);

        ContractAssert.Equal(IdempotencyClaimStatus.PayloadMismatch, (await store.TryClaimAsync(key, "fp-b", CancellationToken.None)).Status,
            "Status of a claim that reuses a completed key with a different fingerprint");
        var replay = await store.TryClaimAsync(key, "fp-a", CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.Completed, replay.Status, "Status of a claim that reuses a completed key with the same fingerprint");
        ContractAssert.SequenceEqual(new byte[] { 1, 2, 3 }, replay.StoredResult!, "The stored result is still replayed under the same fingerprint");
    }

    /// <summary>Contract: a missing fingerprint on either side disables the comparison; the claim is judged on the key alone.</summary>
    [Fact]
    public async Task A_missing_fingerprint_on_either_side_disables_the_comparison()
    {
        var store = await CreateStoreAsync();

        var claimedWithout = NewKey();
        await store.TryClaimAsync(claimedWithout, null, CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(claimedWithout, "fp-a", CancellationToken.None)).Status,
            "Status when the original claim carried no fingerprint");

        var claimedWith = NewKey();
        await store.TryClaimAsync(claimedWith, "fp-a", CancellationToken.None);
        ContractAssert.Equal(IdempotencyClaimStatus.InProgress, (await store.TryClaimAsync(claimedWith, null, CancellationToken.None)).Status,
            "Status when the duplicate carries no fingerprint");
    }

    /// <summary>Contract: releasing a claim forgets its fingerprint too, so the key can be claimed again with any payload.</summary>
    [Fact]
    public async Task Releasing_a_claim_forgets_its_fingerprint()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        var claim = await store.TryClaimAsync(key, "fp-a", CancellationToken.None);

        await store.ReleaseAsync(key, claim.Token!, CancellationToken.None);

        Assert.True((await store.TryClaimAsync(key, "fp-b", CancellationToken.None)).IsClaimed,
            "A released key must be claimable again under a different fingerprint.");
    }
}

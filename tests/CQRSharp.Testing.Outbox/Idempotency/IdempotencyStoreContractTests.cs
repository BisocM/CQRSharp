using CQRSharp.Abstractions.Interfaces.Idempotency;
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

        (await store.TryClaimAsync(NewKey(), CancellationToken.None)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task TryClaim_on_an_already_claimed_key_is_rejected_as_a_duplicate()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue();
        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeFalse("the key is already claimed");
    }

    [SkippableFact]
    public async Task Releasing_a_claim_makes_the_key_claimable_again()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue();
        await store.ReleaseAsync(key, CancellationToken.None);
        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue("a released key is free to be claimed again");
    }

    [SkippableFact]
    public async Task Releasing_an_unknown_key_is_a_no_op()
    {
        var store = await CreateStoreAsync();

        var act = () => store.ReleaseAsync(NewKey(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task Concurrent_claims_of_the_same_key_yield_exactly_one_winner()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.TryClaimAsync(key, CancellationToken.None))));

        results.Count(claimed => claimed).Should().Be(1, "exactly one concurrent caller may claim a given key");
    }
}

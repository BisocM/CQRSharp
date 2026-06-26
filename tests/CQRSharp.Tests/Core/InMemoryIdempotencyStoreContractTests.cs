using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Options;
using CQRSharp.Testing.Idempotency;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Runs the shared <see cref="IdempotencyStoreContractTests" /> against the in-process InMemoryIdempotencyStore,
///     plus an in-memory-specific test that a claim ages out of the retention window.
/// </summary>
public sealed class InMemoryIdempotencyStoreContractTests : IdempotencyStoreContractTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly TimeSpan _retention = TimeSpan.FromMinutes(30);

    protected override Task<IIdempotencyStore> CreateStoreAsync()
        => Task.FromResult<IIdempotencyStore>(new InMemoryIdempotencyStore(
            _time, Options.Create(new InMemoryIdempotencyStoreOptions { Retention = _retention })));

    [Fact]
    public async Task A_claim_is_forgotten_once_the_retention_window_elapses()
    {
        var store = new InMemoryIdempotencyStore(
            _time, Options.Create(new InMemoryIdempotencyStoreOptions { Retention = _retention }));
        const string key = "retention-key";

        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue();
        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeFalse("still within the retention window");

        _time.Advance(_retention + TimeSpan.FromSeconds(1));
        (await store.TryClaimAsync(key, CancellationToken.None)).Should().BeTrue("the claim aged out of the retention window");
    }
}

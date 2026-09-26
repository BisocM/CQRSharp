using CQRSharp.Core.Idempotency;
using CQRSharp.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The shipped in-memory idempotency store, with a switch per operation that makes it throw: the behaviors promise
///     that their bookkeeping never replaces a request's own outcome, and a store that fails is how that is tested. It
///     can also break the contract by answering a claim with nothing, and it records the fingerprints it was asked to
///     claim with.
/// </summary>
internal sealed class FaultyIdempotencyStore : IIdempotencyStore
{
    private readonly InMemoryIdempotencyStore _inner = new(new FakeTimeProvider(), Options.Create(new InMemoryIdempotencyStoreOptions()));
    private readonly List<string?> _fingerprints = [];

    /// <summary>Every claim is answered with <c>null</c> instead of a claim, as a broken store would.</summary>
    public bool AnswersNoClaim { get; set; }

    /// <summary>Thrown by every claim until cleared.</summary>
    public Exception? ClaimFailure { get; set; }

    /// <summary>Thrown by every completion until cleared.</summary>
    public Exception? CompleteFailure { get; set; }

    /// <summary>Thrown by every release until cleared.</summary>
    public Exception? ReleaseFailure { get; set; }

    /// <summary>The fingerprint of every claim asked for, in order.</summary>
    public IReadOnlyList<string?> Fingerprints
    {
        get
        {
            lock (_fingerprints) return _fingerprints.ToArray();
        }
    }

    public Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken)
    {
        lock (_fingerprints) _fingerprints.Add(fingerprint);
        if (AnswersNoClaim) return Task.FromResult<IdempotencyClaim>(null!);
        return ClaimFailure is { } failure
            ? Task.FromException<IdempotencyClaim>(failure)
            : _inner.TryClaimAsync(key, fingerprint, cancellationToken);
    }

    public Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken)
        => CompleteFailure is { } failure ? Task.FromException(failure) : _inner.CompleteAsync(key, claimToken, result, cancellationToken);

    public Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken)
        => ReleaseFailure is { } failure ? Task.FromException(failure) : _inner.ReleaseAsync(key, claimToken, cancellationToken);
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core row that records a single claimed idempotency key. One row exists per live claim; the row is
///     deleted on release, taken over by a later claim once it ages past its expiry, and deleted by the retention
///     service after that. <see cref="ExpiresAt" /> is the UTC instant at which the claim stops deduplicating, which also
///     lets a crashed claimant's key free itself.
/// </summary>
/// <remarks>
///     The type is unsealed, its properties are virtual and it raises its own change notifications, so a context that
///     uses EF Core's lazy-loading or change-tracking proxies (<c>UseLazyLoadingProxies</c>,
///     <c>UseChangeTrackingProxies</c>) can map it.
/// </remarks>
public class IdempotencyEntity : INotifyPropertyChanging, INotifyPropertyChanged
{
    private string _key = string.Empty;
    private DateTime _expiresAt;
    private uint _rowVersion;
    private bool _completed;
    private byte[]? _result;
    private string? _fingerprint;

    /// <inheritdoc />
    public event PropertyChangingEventHandler? PropertyChanging;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The idempotency key identifying the logical request (primary key, supplied by the caller, never database-generated).</summary>
    public virtual string Key { get => _key; set => Set(ref _key, value); }

    /// <summary>
    ///     The UTC instant at which this claim expires. While the current time is before this, the key is a live claim
    ///     and a repeat is treated as a duplicate (answered with the stored result, or rejected); at or after it the claim
    ///     is aged out and may be taken over.
    /// </summary>
    public virtual DateTime ExpiresAt { get => _expiresAt; set => Set(ref _expiresAt, value); }

    /// <summary>
    ///     Optimistic-concurrency token. Configured as a manual row-version that the store increments when it takes
    ///     over an expired row; if two processes both read the same expired row and both try to save the take-over,
    ///     the second save sees a stale token and EF raises a concurrency exception, which is exactly how the take-over
    ///     is kept atomic so two claimants can never both revive the same expired key. (The INSERT of a fresh key is
    ///     instead made race-safe by the unique primary key, not this token.)
    /// </summary>
    public virtual uint RowVersion { get => _rowVersion; set => Set(ref _rowVersion, value); }

    /// <summary>
    ///     Whether the request that claimed the key ran to completion. A live, completed key answers a duplicate with
    ///     <see cref="Result" />; a live key that is not completed means the original is still running (or crashed).
    /// </summary>
    public virtual bool Completed { get => _completed; set => Set(ref _completed, value); }

    /// <summary>The serialized result to replay to duplicates; null when the completed request stored none.</summary>
    public virtual byte[]? Result { get => _result; set => Set(ref _result, value); }

    /// <summary>The payload fingerprint the key was claimed with, or null when the request had none.</summary>
    public virtual string? Fingerprint { get => _fingerprint; set => Set(ref _fingerprint, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        => ChangeNotifications.Set(this, ref field, value, PropertyChanging, PropertyChanged, propertyName);
}

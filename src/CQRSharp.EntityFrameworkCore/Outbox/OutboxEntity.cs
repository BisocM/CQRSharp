using System.ComponentModel;
using System.Runtime.CompilerServices;
using CQRSharp.Persistence;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The mutable EF Core row that persists a single outbox message: one delivery of one notification to one handler.
///     Mapping to and from the immutable public <see cref="OutboxMessage" /> transport record lives in the mapper, so
///     this type carries only persistence concerns. It additionally tracks store-internal ordering, lease and
///     concurrency state that has no place on the public record: an insertion-ordered surrogate key
///     (<see cref="Sequence" />), a visibility lease (<see cref="LockedUntil" />) and an optimistic-concurrency token
///     (<see cref="RowVersion" />) used to make the claim race-safe.
/// </summary>
/// <remarks>
///     The type is unsealed, its properties are virtual and it raises its own change notifications, so a context that
///     uses EF Core's lazy-loading or change-tracking proxies (<c>UseLazyLoadingProxies</c>,
///     <c>UseChangeTrackingProxies</c>) can map it.
/// </remarks>
public class OutboxEntity : INotifyPropertyChanging, INotifyPropertyChanged
{
    private long _sequence;
    private Guid _id;
    private Guid? _notificationId;
    private string _notificationType = string.Empty;
    private string _handlerName = string.Empty;
    private string? _partitionKey;
    private byte[] _payload = [];
    private DateTime _createdAt;
    private OutboxMessageStatus _status;
    private DateTime? _processedAt;
    private string? _lastError;
    private int _attemptCount;
    private DateTime? _nextRetryAt;
    private DateTime? _failedAt;
    private string? _traceParent;
    private DateTime? _lockedUntil;
    private uint _rowVersion;

    /// <inheritdoc />
    public event PropertyChangingEventHandler? PropertyChanging;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     The database-generated, insertion-ordered primary key. Claims are ordered by <see cref="CreatedAt" /> and then
    ///     by this column, and a partitioned message is held back while an earlier row (in that order) with the same
    ///     partition key and handler is unfinished.
    /// </summary>
    public virtual long Sequence { get => _sequence; set => Set(ref _sequence, value); }

    /// <summary>The unique identifier of the message (an alternate key, supplied by the caller, never database-generated).</summary>
    public virtual Guid Id { get => _id; set => Set(ref _id, value); }

    /// <summary>The identifier shared by every delivery one publish produced, or null for a message stored by other means.</summary>
    public virtual Guid? NotificationId { get => _notificationId; set => Set(ref _notificationId, value); }

    /// <summary>The stable notification name used to resolve the notification type at dispatch time.</summary>
    public virtual string NotificationType { get => _notificationType; set => Set(ref _notificationType, value); }

    /// <summary>The stable name of the handler this message is delivered to.</summary>
    public virtual string HandlerName { get => _handlerName; set => Set(ref _handlerName, value); }

    /// <summary>The ordering key, or null for a message delivered without ordering.</summary>
    public virtual string? PartitionKey { get => _partitionKey; set => Set(ref _partitionKey, value); }

    /// <summary>The serialized notification body.</summary>
    public virtual byte[] Payload { get => _payload; set => Set(ref _payload, value); }

    /// <summary>The UTC timestamp at which the message was created; the primary FIFO ordering key for claims.</summary>
    public virtual DateTime CreatedAt { get => _createdAt; set => Set(ref _createdAt, value); }

    /// <summary>The current processing status of the message.</summary>
    public virtual OutboxMessageStatus Status { get => _status; set => Set(ref _status, value); }

    /// <summary>The UTC timestamp at which the message was processed, or null if not yet processed.</summary>
    public virtual DateTime? ProcessedAt { get => _processedAt; set => Set(ref _processedAt, value); }

    /// <summary>Error detail from the most recent failed delivery attempt, if any.</summary>
    public virtual string? LastError { get => _lastError; set => Set(ref _lastError, value); }

    /// <summary>The number of failed delivery attempts recorded so far; persisted so retry limits survive restarts.</summary>
    public virtual int AttemptCount { get => _attemptCount; set => Set(ref _attemptCount, value); }

    /// <summary>The earliest UTC time a failed message may be claimed again, or null to make it immediately eligible.</summary>
    public virtual DateTime? NextRetryAt { get => _nextRetryAt; set => Set(ref _nextRetryAt, value); }

    /// <summary>
    ///     The UTC timestamp at which the message was dead-lettered, or null; what the dead-letter retention measures by. A
    ///     dead letter without one (dead-lettered before the column existed) counts as older than any cut-off.
    /// </summary>
    public virtual DateTime? FailedAt { get => _failedAt; set => Set(ref _failedAt, value); }

    /// <summary>The W3C traceparent captured at enqueue time so the dispatch span can link to the originating trace.</summary>
    public virtual string? TraceParent { get => _traceParent; set => Set(ref _traceParent, value); }

    /// <summary>
    ///     Store-internal visibility lease: while a message is in progress this holds the UTC time until which the
    ///     claim is held. A message stuck in progress past this time is treated as abandoned (its claimant crashed)
    ///     and becomes claimable again. Kept off the public <see cref="OutboxMessage" /> record because it is an
    ///     implementation detail of this store's reclaim logic, not transport state.
    /// </summary>
    public virtual DateTime? LockedUntil { get => _lockedUntil; set => Set(ref _lockedUntil, value); }

    /// <summary>
    ///     Optimistic-concurrency token. Configured as a manual row-version that the store changes on every write, so
    ///     "the row still carries the version I wrote" means "nobody has touched it since": if two processors load the
    ///     same row and both try to claim it, the second write sees a stale token and loses, which is exactly how the
    ///     atomic claim is enforced.
    /// </summary>
    public virtual uint RowVersion { get => _rowVersion; set => Set(ref _rowVersion, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        => ChangeNotifications.Set(this, ref field, value, PropertyChanging, PropertyChanged, propertyName);
}

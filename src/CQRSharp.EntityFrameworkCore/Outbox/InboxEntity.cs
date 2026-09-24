using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core row that records one completed outbox delivery: message <see cref="MessageId" /> reached handler
///     <see cref="HandlerName" />. Its presence is what makes a redelivery of the same message a no-op. Written through
///     the same <c>DbContext</c> the handler uses, so with a unit of work over that context the record commits
///     atomically with the handler's changes. Rows age out after the store's inbox retention.
/// </summary>
/// <remarks>
///     The type is unsealed, its properties are virtual and it raises its own change notifications, so a context that
///     uses EF Core's lazy-loading or change-tracking proxies (<c>UseLazyLoadingProxies</c>,
///     <c>UseChangeTrackingProxies</c>) can map it.
/// </remarks>
public class InboxEntity : INotifyPropertyChanging, INotifyPropertyChanged
{
    private Guid _messageId;
    private string _handlerName = string.Empty;
    private DateTime _deliveredAt;

    /// <inheritdoc />
    public event PropertyChangingEventHandler? PropertyChanging;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The delivered outbox message (part of the primary key).</summary>
    public virtual Guid MessageId { get => _messageId; set => Set(ref _messageId, value); }

    /// <summary>The handler the message was delivered to (part of the primary key).</summary>
    public virtual string HandlerName { get => _handlerName; set => Set(ref _handlerName, value); }

    /// <summary>The UTC timestamp at which the delivery was recorded; what the inbox retention measures by.</summary>
    public virtual DateTime DeliveredAt { get => _deliveredAt; set => Set(ref _deliveredAt, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        => ChangeNotifications.Set(this, ref field, value, PropertyChanging, PropertyChanged, propertyName);
}

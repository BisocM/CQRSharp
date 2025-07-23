using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;

namespace CQRSharp.Core.Background.Outbox.Types;

/// <summary>
///     A scoped, in-memory implementation of <see cref="IOutbox" />. It collects notifications
///     within a request scope, to be persisted later by another component.
/// </summary>
internal sealed class Outbox : IOutbox
{
    private readonly List<INotification> _notifications = new();

    /// <inheritdoc />
    public void Add(INotification notification)
    {
        _notifications.Add(notification);
    }

    /// <inheritdoc />
    public IReadOnlyList<INotification> GetNotifications()
    {
        return _notifications;
    }
}
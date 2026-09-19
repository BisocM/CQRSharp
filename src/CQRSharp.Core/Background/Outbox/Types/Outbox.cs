using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;

namespace CQRSharp.Core.Background.Outbox.Types;

/// <summary>
///     A scoped, in-memory implementation of <see cref="IOutbox" />. It collects notifications
///     within a request scope, to be persisted later by another component.
/// </summary>
internal sealed class Outbox : IOutbox
{
    private readonly List<INotification> _notifications = new();

    /// <summary>The number of buffered notifications; a mark for <see cref="TruncateTo" />.</summary>
    internal int Count => _notifications.Count;

    /// <summary>
    ///     Discards everything buffered after <paramref name="mark" /> — the notifications of a failed handler
    ///     attempt — while keeping what was buffered before it (an outer request, or an earlier successful attempt).
    /// </summary>
    internal void TruncateTo(int mark)
    {
        if (mark < 0 || mark >= _notifications.Count) return;
        _notifications.RemoveRange(mark, _notifications.Count - mark);
    }

    /// <inheritdoc />
    public void Add(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _notifications.Add(notification);
    }

    /// <inheritdoc />
    public IReadOnlyList<INotification> GetNotifications()
    {
        return _notifications;
    }

    /// <inheritdoc />
    public IReadOnlyList<INotification> Drain()
    {
        if (_notifications.Count == 0) return Array.Empty<INotification>();

        var drained = _notifications.ToArray();
        _notifications.Clear();
        return drained;
    }
}
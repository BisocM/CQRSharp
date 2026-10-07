namespace CQRSharp.Sample.Domain.Events;

/// <summary>
///     A reminder that falls due at a set time. It is published for later delivery (<c>PublishAt</c> /
///     <c>PublishAfter</c>): the outbox holds it until then, which is why it needs a stable name.
/// </summary>
[NotificationName("sample.reminder.due")]
public sealed record ReminderDueNotification(Guid ReminderId, DateTimeOffset DueAt) : INotification;

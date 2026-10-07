using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

// Records when the reminder reached it, so the self-test can tell that the outbox held it until it was due.
public sealed class ReminderDueNotificationHandler(SampleDiagnostics diagnostics, TimeProvider timeProvider)
    : INotificationHandler<ReminderDueNotification>
{
    public Task Handle(ReminderDueNotification notification, CancellationToken cancellationToken)
    {
        diagnostics.RecordReminderDue(notification, timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }
}

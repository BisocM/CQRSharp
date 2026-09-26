using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

// Records the scope it ran in: the outbox processor delivers in a scope of its own, which is how the self-test tells an
// outbox delivery from an in-process one.
public sealed class UserCreatedNotificationHandler(SampleDiagnostics diagnostics, SampleScopedMarker scope)
    : INotificationHandler<UserCreatedNotification>
{
    public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
    {
        diagnostics.RecordUserCreated(notification, scope.Id);
        return Task.CompletedTask;
    }
}

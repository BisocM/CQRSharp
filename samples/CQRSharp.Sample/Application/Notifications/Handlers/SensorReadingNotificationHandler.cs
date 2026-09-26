using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

public sealed class SensorReadingNotificationHandler(SampleDiagnostics diagnostics) : INotificationHandler<SensorReadingNotification>
{
    public const string RunKey = "sensor-reading";

    public Task Handle(SensorReadingNotification notification, CancellationToken cancellationToken)
    {
        diagnostics.CountRun(RunKey);
        return Task.CompletedTask;
    }
}

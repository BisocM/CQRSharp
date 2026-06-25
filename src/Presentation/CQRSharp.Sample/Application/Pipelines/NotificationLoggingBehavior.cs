using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications.Pipelines;
using CQRSharp.Core.Pipelines;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Pipelines;

public sealed class NotificationLoggingBehavior<TNotification>(
    ILogger<NotificationLoggingBehavior<TNotification>> logger,
    SampleDiagnostics diagnostics)
    : INotificationPipelineBehavior<TNotification>, IPrioritizedPipelineBehavior
    where TNotification : INotification
{
    public int PipelineExecutionPriority => -100;

    public async Task Handle(
        TNotification notification,
        Func<CancellationToken, Task> next,
        CancellationToken cancellationToken)
    {
        var notificationName = typeof(TNotification).Name;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            diagnostics.RecordNotificationPipelineBefore(typeof(TNotification));
            logger.LogInformation("[NotificationPipeline] Publishing {NotificationName}", notificationName);
            await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[NotificationPipeline] Notification {NotificationName} failed", notificationName);
            throw;
        }
        finally
        {
            diagnostics.RecordNotificationPipelineAfter(typeof(TNotification));
            stopwatch.Stop();
            logger.LogInformation(
                "[NotificationPipeline] Published {NotificationName} in {ElapsedMilliseconds}ms",
                notificationName,
                stopwatch.ElapsedMilliseconds);
        }
    }
}

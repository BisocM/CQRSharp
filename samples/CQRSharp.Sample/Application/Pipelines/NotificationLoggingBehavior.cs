using System.Diagnostics;
using CQRSharp.Sample.Infrastructure.Logging;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Pipelines;

/// <summary>
///     An open-generic notification behavior: logs every notification a handler receives and how long the handlers took,
///     whether it is published in-process or delivered by the outbox processor.
/// </summary>
public sealed class NotificationLoggingBehavior<TNotification>(
    ILogger<NotificationLoggingBehavior<TNotification>> logger,
    SampleDiagnostics diagnostics)
    : INotificationPipelineBehavior<TNotification>
    where TNotification : INotification
{
    public async Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
    {
        diagnostics.RecordNotificationPipelineBefore(typeof(TNotification));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(cancellationToken);
        }
        finally
        {
            SampleLog.NotificationHandled(logger, typeof(TNotification).Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            diagnostics.RecordNotificationPipelineAfter(typeof(TNotification));
        }
    }
}

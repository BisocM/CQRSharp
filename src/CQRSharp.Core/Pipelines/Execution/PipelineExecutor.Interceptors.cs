using CQRSharp.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines;

internal sealed partial class PipelineExecutor
{
    /// <summary>
    ///     Runs every post-handler, in the plan's priority order, the way nested <c>finally</c> blocks run: one that throws
    ///     neither stops the ones after it nor hides the failure they observe. On a request that had succeeded, the first
    ///     post-handler failure becomes the request's failure: it is returned for the caller to receive, and the
    ///     post-handlers after it observe it as the outcome. Every other post-handler failure is logged.
    /// </summary>
    /// <returns>The failure a post-handler turned a successful request into, or <c>null</c>.</returns>
    private async Task<Exception?> InvokePostHandlersAsync<TRequest>(
        IPostHandlerAttribute[] postHandlers,
        TRequest request,
        RequestOutcome outcome,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        Exception? failure = null;
        for (var i = 0; i < postHandlers.Length; i++)
        {
            var postHandler = postHandlers[i];
            try
            {
                // A post-handler observing a failure runs on the failure path, where the request's token may be the one
                // that was cancelled: honouring it would stop the very audit that must record the failure.
                await postHandler.OnAfterHandle(request, outcome, services, outcome.Threw ? CancellationToken.None : cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!outcome.Threw)
            {
                failure = ex;
                outcome = RequestOutcome.FromException(ex);
            }
            catch (Exception ex)
            {
                LogPostHandlerFailed(_logger, ex, postHandler.GetType().Name, typeof(TRequest).Name);
            }
        }

        return failure;
    }

    /// <summary>
    ///     Publishes a request's terminal failure notification. It goes out under no cancellation token: the request's own
    ///     may be the one that was cancelled (by its caller, or by a timeout's deadline), and a subscriber auditing the
    ///     failure must still be able to run. A subscriber that fails is logged and never replaces the exception the
    ///     request's caller is about to receive.
    /// </summary>
    private async Task PublishTerminalFailureAsync<TRequest, TNotification>(INotificationDispatcher lifecycle, TNotification notification)
        where TRequest : IRequest
        where TNotification : INotification
    {
        try
        {
            await lifecycle.Publish(notification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTerminalNotificationFailed(_logger, ex, typeof(TNotification).Name, typeof(TRequest).Name);
        }
    }

    [LoggerMessage(1000, LogLevel.Warning,
        "A {NotificationName} subscriber failed for {RequestName}; the request's own failure is what its caller receives.")]
    private static partial void LogTerminalNotificationFailed(ILogger logger, Exception exception, string notificationName, string requestName);

    [LoggerMessage(1001, LogLevel.Warning,
        "Post-handler {PostHandlerName} failed for {RequestName}, which had already failed; the request's own failure is what its caller receives.")]
    private static partial void LogPostHandlerFailed(ILogger logger, Exception exception, string postHandlerName, string requestName);

    [LoggerMessage(1002, LogLevel.Warning,
        "Disposing the stream of {RequestName}, which had already failed, failed as well; the stream's own failure is what its consumer receives.")]
    private static partial void LogStreamDisposalFailed(ILogger logger, Exception exception, string requestName);
}

namespace CQRSharp.Pipelines;

/// <summary>
///     The continuation a notification pipeline behavior invokes to run the next behavior (or, at the end of the chain,
///     the delivery to the handlers). The cancellation token is defaulted, so a behavior can simply <c>await next()</c> to
///     flow the ambient token, or pass its own token to override it, exactly as with request and stream behaviors.
/// </summary>
public delegate Task NotificationHandlerDelegate(CancellationToken cancellationToken = default);

/// <summary>
///     A behavior that wraps the delivery of a notification. Behaviors are resolved for the notification's runtime
///     type: a closed <c>INotificationPipelineBehavior&lt;OrderPlaced&gt;</c> runs for every <c>OrderPlaced</c>, whether it
///     was published as itself, as a base type or as <see cref="INotification" />, and an open-generic behavior is closed
///     over the runtime type. (A runtime type no source-generated module knows, because it is declared and handled only
///     where the generator does not run, gets the behaviors of the type it was published as, or of the handler's
///     declared type on an outbox delivery.) They are ordered by <see cref="IPrioritizedPipelineBehavior" />. An in-process
///     publish runs them once around the whole handler fan-out, even when nothing handles the notification; an outbox
///     delivery runs them around its one handler.
/// </summary>
/// <typeparam name="TNotification">The type of the notification.</typeparam>
public interface INotificationPipelineBehavior<in TNotification>
    where TNotification : INotification
{
    /// <summary>
    ///     Handles the notification by invoking the next behavior in the pipeline or the delivery to the handlers.
    /// </summary>
    /// <param name="notification">The notification being published.</param>
    /// <param name="next">The continuation; call <c>await next()</c> to run the rest of the pipeline.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that completes when the rest of the pipeline has.</returns>
    Task Handle(
        TNotification notification,
        NotificationHandlerDelegate next,
        CancellationToken cancellationToken);
}

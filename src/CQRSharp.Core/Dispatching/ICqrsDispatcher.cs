namespace CQRSharp;

/// <summary>
///     The dispatcher applications use: sends commands and queries, runs streams and publishes notifications. It is
///     scoped; resolve it from the scope the work belongs to (the HTTP request's, a message's), because the request's
///     context factory, and by default its handler and behaviors, are resolved from that scope.
/// </summary>
public interface ICqrsDispatcher
{
    /// <summary>
    ///     Sends a command or query through its pipeline to its handler and returns the handler's result. Where it runs
    ///     follows <see cref="DispatcherOptions.RunMode" /> and <see cref="DispatcherOptions.ScopeMode" />.
    /// </summary>
    /// <typeparam name="TResponse">The response the request is declared with.</typeparam>
    /// <param name="request">The request; a new instance per dispatch, since the dispatcher sets its context.</param>
    /// <param name="cancellationToken">A token the handler and the behaviors observe.</param>
    /// <returns>The handler's result, or the result an exception handler or an idempotency replay supplied.</returns>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="request" /> is a stream request (use <see cref="Stream{TItem}" />), or no handler is routed for
    ///     its type.
    /// </exception>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sends a request known only at run time, exactly as <see cref="Send{TResponse}" /> sends it as its runtime type,
    ///     and returns the boxed result.
    /// </summary>
    /// <param name="request">The request; it must implement <see cref="IRequest" />.</param>
    /// <param name="cancellationToken">A token the handler and the behaviors observe.</param>
    /// <returns>The boxed result.</returns>
    /// <exception cref="ArgumentException"><paramref name="request" /> does not implement <see cref="IRequest" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="request" /> is a stream request (use <see cref="Stream(object, CancellationToken)" />), or no
    ///     handler is routed for its type.
    /// </exception>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs a streaming request through its pipeline. Nothing runs until the stream is enumerated; it runs on the flow
    ///     that enumerates it, in every <see cref="RunMode" />, in the scope <see cref="DispatcherOptions.ScopeMode" />
    ///     gives it.
    /// </summary>
    /// <typeparam name="TItem">The item type the stream yields.</typeparam>
    /// <param name="request">The streaming request; a new instance per dispatch.</param>
    /// <param name="cancellationToken">
    ///     A token the stream observes, together with the one it is enumerated with (<c>WithCancellation</c>).
    /// </param>
    /// <returns>The stream of items.</returns>
    IAsyncEnumerable<TItem> Stream<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs a streaming request known only at run time, exactly as <see cref="Stream{TItem}" /> runs it as its runtime
    ///     type, and yields its items boxed.
    /// </summary>
    /// <param name="request">The streaming request; it must implement <see cref="IStreamRequest" />.</param>
    /// <param name="cancellationToken">A token the stream observes.</param>
    /// <returns>The stream of boxed items.</returns>
    /// <exception cref="ArgumentException"><paramref name="request" /> does not implement <see cref="IStreamRequest" />.</exception>
    IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a notification to every handler of its runtime type, its base types and its interfaces. While the
    ///     outbox routes notifications (<see cref="OutboxMode" />) and the registered
    ///     <see cref="CQRSharp.Persistence.INotificationSerializer" /> names the notification, it is stored in the outbox
    ///     for durable delivery instead: buffered while a request of this scope (or an outbox delivery) runs and stored
    ///     when that succeeds, or stored at once when published outside one. Any other notification is delivered
    ///     in-process before the returned task completes.
    /// </summary>
    /// <typeparam name="TNotification">The type the notification is published as; delivery follows its runtime type.</typeparam>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">A token the handlers observe.</param>
    /// <returns>A task that completes once the notification is delivered in-process, buffered or stored.</returns>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}

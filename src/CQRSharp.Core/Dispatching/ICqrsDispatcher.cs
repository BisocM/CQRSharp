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

    /// <summary>
    ///     Publishes a notification for delivery no earlier than <paramref name="dueAt" />: it is stored in the outbox
    ///     exactly as <see cref="Publish{TNotification}" /> stores a durable notification (buffered while a request of
    ///     this scope, or an outbox delivery, runs and stored when that succeeds, or stored at once outside one), and the
    ///     outbox processor delivers it to each of its handlers once the time has come, as if it had been published then.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the outbox can hold a notification until it is due, so a scheduled publish never falls back to
    ///         in-process delivery: it fails, before anything is buffered or stored, when the outbox is off
    ///         (<see cref="OutboxMode.Disabled" />), when the registered <see cref="CQRSharp.Persistence.INotificationSerializer" />
    ///         does not name the notification (it is not durable), or when the registered outbox store cannot schedule
    ///         (it does not implement <see cref="CQRSharp.Persistence.ISchedulingOutboxStore" />). Under
    ///         <see cref="OutboxMode.Transactional" /> it goes to the outbox whether or not a transaction is active: inside
    ///         one it is stored with the commit, outside one it is stored as the <see cref="OutboxMode.Enabled" /> mode
    ///         stores it.
    ///     </para>
    ///     <para>
    ///         A due time that has already passed when the notification is stored makes it an ordinary durable publish,
    ///         delivered as soon as the processor claims it. Delivery is at least once, and up to the processor's polling
    ///         interval after the due time. An implementation that cannot schedule inherits this default, which throws
    ///         <see cref="NotSupportedException" />.
    ///     </para>
    /// </remarks>
    /// <typeparam name="TNotification">The type the notification is published as; delivery follows its runtime type.</typeparam>
    /// <param name="notification">The notification.</param>
    /// <param name="dueAt">The earliest time it may be delivered.</param>
    /// <param name="cancellationToken">A token to cancel storing it.</param>
    /// <returns>A task that completes once the notification is buffered or stored.</returns>
    /// <exception cref="NotSupportedException">This implementation of <see cref="ICqrsDispatcher" /> cannot schedule.</exception>
    Task PublishAt<TNotification>(TNotification notification, DateTimeOffset dueAt, CancellationToken cancellationToken = default)
        where TNotification : INotification
        => throw SchedulingNotSupported(GetType());

    /// <summary>
    ///     Publishes a notification for delivery once <paramref name="delay" /> has passed, measured from now on the
    ///     application's <see cref="TimeProvider" />: exactly <see cref="PublishAt{TNotification}" /> with that due time.
    /// </summary>
    /// <remarks>An implementation that cannot schedule inherits this default, which throws <see cref="NotSupportedException" />.</remarks>
    /// <typeparam name="TNotification">The type the notification is published as; delivery follows its runtime type.</typeparam>
    /// <param name="notification">The notification.</param>
    /// <param name="delay">How long from now it must wait at least; zero for as soon as possible.</param>
    /// <param name="cancellationToken">A token to cancel storing it.</param>
    /// <returns>A task that completes once the notification is buffered or stored.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay" /> is negative, or reaches past the calendar's end.</exception>
    /// <exception cref="NotSupportedException">This implementation of <see cref="ICqrsDispatcher" /> cannot schedule.</exception>
    Task PublishAfter<TNotification>(TNotification notification, TimeSpan delay, CancellationToken cancellationToken = default)
        where TNotification : INotification
        => throw SchedulingNotSupported(GetType());

    // The default of the scheduling members, for an implementation written before they existed.
    private static NotSupportedException SchedulingNotSupported(Type implementation)
        => new($"'{implementation.FullName}' does not implement scheduled publishing ({nameof(ICqrsDispatcher)}.{nameof(PublishAt)} / " +
               $"{nameof(PublishAfter)}). The dispatcher AddCqrsGenerated registers does; a decorator or fake of {nameof(ICqrsDispatcher)} " +
               "must implement both itself.");
}

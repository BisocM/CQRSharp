namespace CQRSharp;

/// <summary>
///     Published when a started stream fails after producing zero or more items: a pre-handler, the handler's stream or
///     a post-handler threw, the stream was cancelled, or it ran out of time.
/// </summary>
/// <remarks>
///     A <see cref="StreamInitiatedNotification{TItem}" /> is followed by <see cref="StreamCompletedNotification{TItem}" />
///     when the stream is enumerated to its end, or by <see cref="StreamFailedNotification{TItem}" /> when it fails. A
///     consumer that stops enumerating early, without an error, ends the stream with neither. The notification is
///     delivered under no cancellation token, since the stream's own may be the one that was cancelled; a subscriber that
///     fails is logged and never replaces the stream's exception.
/// </remarks>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamFailedNotification<TItem> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StreamFailedNotification{TItem}" /> class.
    /// </summary>
    /// <param name="request">The streaming request that failed.</param>
    /// <param name="itemsYielded">The number of items yielded before the failure.</param>
    /// <param name="exception">The exception the stream failed with, which its consumer receives.</param>
    public StreamFailedNotification(IStreamRequest<TItem> request, long itemsYielded, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);
        Request = request;
        RequestName = request.GetType().Name;
        ItemsYielded = itemsYielded;
        Exception = exception;
    }

    /// <summary>Gets the streaming request that failed.</summary>
    public IStreamRequest<TItem> Request { get; }

    /// <summary>Gets the name of the request's type.</summary>
    public string RequestName { get; }

    /// <summary>Gets the number of items yielded before the failure.</summary>
    public long ItemsYielded { get; }

    /// <summary>Gets the exception the stream failed with.</summary>
    public Exception Exception { get; }
}

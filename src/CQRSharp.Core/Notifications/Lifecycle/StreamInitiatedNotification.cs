namespace CQRSharp;

/// <summary>
///     Published when a stream starts, as its consumer asks for the first item and before its pre-handlers run.
/// </summary>
/// <remarks>
///     <see cref="StreamCompletedNotification{TItem}" /> follows it when the stream is enumerated to its end, and
///     <see cref="StreamFailedNotification{TItem}" /> when it fails. A consumer that stops enumerating early, without an
///     error, ends the stream with neither.
/// </remarks>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamInitiatedNotification<TItem> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StreamInitiatedNotification{TItem}" /> class.
    /// </summary>
    /// <param name="request">The streaming request being started.</param>
    public StreamInitiatedNotification(IStreamRequest<TItem> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        RequestName = request.GetType().Name;
    }

    /// <summary>Gets the streaming request being started.</summary>
    public IStreamRequest<TItem> Request { get; }

    /// <summary>Gets the name of the request's type.</summary>
    public string RequestName { get; }
}

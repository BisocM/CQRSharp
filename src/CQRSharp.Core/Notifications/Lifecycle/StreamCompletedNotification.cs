namespace CQRSharp;

/// <summary>
///     Published when a stream completed: it was enumerated to its end and its post-handlers ran without throwing.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamCompletedNotification<TItem> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StreamCompletedNotification{TItem}" /> class.
    /// </summary>
    /// <param name="request">The streaming request that completed.</param>
    /// <param name="itemsYielded">The number of items the stream produced.</param>
    public StreamCompletedNotification(IStreamRequest<TItem> request, long itemsYielded)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        RequestName = request.GetType().Name;
        ItemsYielded = itemsYielded;
    }

    /// <summary>Gets the streaming request that completed.</summary>
    public IStreamRequest<TItem> Request { get; }

    /// <summary>Gets the name of the request's type.</summary>
    public string RequestName { get; }

    /// <summary>Gets the number of items the stream produced.</summary>
    public long ItemsYielded { get; }
}

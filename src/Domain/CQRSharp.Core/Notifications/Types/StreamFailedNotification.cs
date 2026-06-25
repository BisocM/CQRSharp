using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Published when a streaming request faults after producing zero or more items.
/// </summary>
/// <remarks>
///     Complements <see cref="StreamCompletedNotification{TItem}" />. A <see cref="StreamInitiatedNotification{TItem}" />
///     is followed by <see cref="StreamCompletedNotification{TItem}" /> when the sequence is fully consumed, or by
///     <see cref="StreamFailedNotification{TItem}" /> when it faults. Note that neither terminal notification is
///     published if the consumer simply stops enumerating early without an error.
/// </remarks>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamFailedNotification<TItem> : INotification
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StreamFailedNotification{TItem}" /> class.
    /// </summary>
    /// <param name="request">The streaming request that faulted.</param>
    /// <param name="itemsYielded">The number of items yielded before the fault.</param>
    /// <param name="exception">The exception that faulted the stream.</param>
    public StreamFailedNotification(IStreamRequest<TItem> request, long itemsYielded, Exception exception)
    {
        Request = request;
        RequestName = request.GetType().Name;
        ItemsYielded = itemsYielded;
        Exception = exception;
    }

    /// <summary>Gets the streaming request that faulted.</summary>
    public IStreamRequest<TItem> Request { get; }

    /// <summary>Gets the type name of the faulted request.</summary>
    public string RequestName { get; }

    /// <summary>Gets the number of items yielded before the fault.</summary>
    public long ItemsYielded { get; }

    /// <summary>Gets the exception that faulted the stream.</summary>
    public Exception Exception { get; }
}

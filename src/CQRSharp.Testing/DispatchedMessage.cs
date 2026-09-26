namespace CQRSharp.Testing;

/// <summary>How a message reached a <see cref="RecordingCqrsDispatcher" />.</summary>
public enum DispatchKind
{
    /// <summary>The message was passed to one of the <c>Send</c> overloads.</summary>
    Send,

    /// <summary>The message was passed to one of the <c>Stream</c> overloads.</summary>
    Stream,

    /// <summary>The message was passed to <c>Publish</c>.</summary>
    Publish,

    /// <summary>
    ///     The message was passed to <c>PublishAt</c> or <c>PublishAfter</c>, for later delivery; the entry's
    ///     <see cref="DispatchedMessage.DueAt" /> says when.
    /// </summary>
    ScheduledPublish
}

/// <summary>One entry in the ordered log a <see cref="RecordingCqrsDispatcher" /> keeps of everything dispatched through it.</summary>
/// <param name="Kind">Which dispatcher operation received the message.</param>
/// <param name="Message">The request or notification instance exactly as the code under test passed it.</param>
public readonly record struct DispatchedMessage(DispatchKind Kind, object Message)
{
    /// <summary>
    ///     When a <see cref="DispatchKind.ScheduledPublish" /> is due: the time passed to <c>PublishAt</c>, or the
    ///     recording dispatcher's clock plus the delay passed to <c>PublishAfter</c>. <see langword="null" /> for every other
    ///     kind.
    /// </summary>
    public DateTimeOffset? DueAt { get; init; }
}

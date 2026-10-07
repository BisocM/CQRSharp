using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>What <see cref="INotificationIntake.AcceptAsync" /> did with a received notification.</summary>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public enum IntakeOutcome
{
    /// <summary>Stored for its local handlers: acknowledge it.</summary>
    Stored,

    /// <summary>Taken in before (the inbox knows its message id): acknowledge it, nothing was stored again.</summary>
    Duplicate,

    /// <summary>No local handler receives it, so nothing was stored: acknowledge it.</summary>
    NoSubscribers,

    /// <summary>
    ///     The application does not know its name. Hold it and try again after <see cref="IntakeResult.RetryAfter" /> while
    ///     that is set (a newer instance of a fleet running mixed versions may know it); reject it when it is not.
    /// </summary>
    UnknownNotification,

    /// <summary>It carries no name, or its payload cannot be read: no retry can help, so reject it.</summary>
    UnreadablePayload
}

using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>How a send through an <see cref="INotificationTransport" /> ended; see <see cref="TransportSendResult" />.</summary>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public enum TransportSendStatus
{
    /// <summary>The destination took the message; it is marked processed.</summary>
    Sent = 1,

    /// <summary>The destination could not be reached; the message is sent again later, and no attempt is charged.</summary>
    Unavailable = 2,

    /// <summary>The destination refused the message; an attempt is charged, and the message is dead-lettered when the attempts run out.</summary>
    Rejected = 3
}

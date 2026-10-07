using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Transports;

/// <summary>
///     What a transport's configuration names: the notifications it forwards and the ones it takes in, by type or by stable
///     name. CQRSharp checks it against the application without sending or receiving anything: a forwarded type the
///     serializer does not name (<c>CQRCONF014</c>), a forwarded notification that <c>Transactional</c> mode would deliver
///     in-process outside a transaction (<c>CQRCONF016</c>), a notification taken in that no local handler receives
///     (<c>CQRCONF017</c>). A name matched by a pattern (a broker wildcard) is not listed, since no check can resolve it.
/// </summary>
[Experimental(TransportExperiment.DiagnosticId, UrlFormat = TransportExperiment.UrlFormat)]
public sealed class NotificationTransportDeclaration
{
    /// <summary>A transport that declares nothing.</summary>
    public static NotificationTransportDeclaration None { get; } = new();

    /// <summary>The notification types the transport is configured to forward by type.</summary>
    public IReadOnlyList<Type> PublishedTypes { get; init; } = Array.Empty<Type>();

    /// <summary>The stable notification names the transport is configured to forward by name.</summary>
    public IReadOnlyList<string> PublishedNames { get; init; } = Array.Empty<string>();

    /// <summary>The notification types the transport is configured to take in by type.</summary>
    public IReadOnlyList<Type> ConsumedTypes { get; init; } = Array.Empty<Type>();

    /// <summary>The exact stable notification names the transport is configured to take in by name.</summary>
    public IReadOnlyList<string> ConsumedNames { get; init; } = Array.Empty<string>();
}

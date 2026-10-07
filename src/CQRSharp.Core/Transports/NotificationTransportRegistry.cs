using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using CQRSharp.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CQRSharp.Core.Transports;

/// <summary>
///     The service provider's notification transports: which of them a published notification is forwarded through (asked
///     of each transport once per notification type, then kept), and which one a claimed outbox message is addressed to.
///     Empty, and free, when no transport is registered.
/// </summary>
/// <remarks>
///     Built once per provider, when the outbox or the dispatcher is first used. A transport name that is invalid or clashes
///     with a handler's or another transport's (<c>CQRCONF015</c>) is kept as the registry's <see cref="Failure" />, which
///     fails every use of the outbox that could reach a transport (storing a message, the processor starting), since a message
///     addressed to that name could reach the wrong recipient. Building the registry never throws, so the startup validator,
///     which reports the clash itself, still reports everything else.
/// </remarks>
internal sealed class NotificationTransportRegistry
{
    /// <summary>The registry of a provider without transports.</summary>
    public static NotificationTransportRegistry Empty { get; } = new([], null);

    private readonly INotificationTransport[] _all;
    private readonly FrozenDictionary<string, INotificationTransport> _byName;
    private readonly FrozenSet<Type> _publishedTypes;
    private readonly ConcurrentDictionary<Type, INotificationTransport[]> _byNotificationType = new();

    private NotificationTransportRegistry(INotificationTransport[] transports, string? failure)
    {
        _all = transports;
        Failure = failure;

        // The first of two transports with one name is kept: with a clash nothing is ever sent anyway (Failure).
        var byName = new Dictionary<string, INotificationTransport>(StringComparer.Ordinal);
        foreach (var transport in transports)
            if (transport.Name is { } name)
                byName.TryAdd(name, transport);
        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _publishedTypes = transports.SelectMany(t => t.Declaration.PublishedTypes).ToFrozenSet();
    }

    /// <summary>Whether no transport is registered.</summary>
    public bool IsEmpty => _all.Length == 0;

    /// <summary>Every registered transport, in registration order.</summary>
    public IReadOnlyList<INotificationTransport> Transports => _all;

    /// <summary>
    ///     The configuration error (<c>CQRCONF015</c>) that makes the transports unusable, or <see langword="null" /> when
    ///     their names are sound.
    /// </summary>
    public string? Failure { get; }

    /// <summary>
    ///     Builds the registry of <paramref name="provider" /> from its <see cref="INotificationTransport" /> registrations,
    ///     and logs, once, what the transports declare to take in that no local handler receives (<c>CQRCONF017</c>).
    /// </summary>
    public static NotificationTransportRegistry Create(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var transports = provider.GetServices<INotificationTransport>().ToArray();
        if (transports.Length == 0) return Empty;

        var subscriptions = provider.GetService<INotificationSubscriptionRegistry>();
        var clash = CqrsConfigurationRules.TransportNameClashes(transports, subscriptions).FirstOrDefault();
        var registry = new NotificationTransportRegistry(transports, clash is null ? null : CqrsConfigurationRules.FailureMessage(clash));

        // The first-use half of CQRCONF017: nothing fails, since the transport acknowledges what nothing here handles, but
        // a binding that brings in notifications no handler receives is worth a warning where the transports come to life.
        // Asked of a scope, as every writer asks it: an application may register its serializer scoped.
        using var scope = provider.CreateScope();
        if (subscriptions is not null && scope.ServiceProvider.GetService<INotificationSerializer>() is { } serializer)
        {
            var logger = provider.GetService<ILoggerFactory>()?.CreateLogger(CqrsConfigurationLog.Category) ?? NullLogger.Instance;
            foreach (var transport in transports)
            foreach (var issue in CqrsConfigurationRules.ConsumedWithoutSubscribers(transport, serializer, subscriptions))
                CqrsConfigurationLog.TransportWarning(logger, issue.Code, transport.Name, issue.Message);
        }

        return registry;
    }

    /// <summary>The transport named <paramref name="handlerName" />, when an outbox message is addressed to one.</summary>
    public bool TryGet(string handlerName, [NotNullWhen(true)] out INotificationTransport? transport)
    {
        if (_all.Length == 0)
        {
            transport = null;
            return false;
        }

        return _byName.TryGetValue(handlerName, out transport);
    }

    /// <summary>
    ///     The transports a notification of <paramref name="notificationType" />, stored as <paramref name="notificationName" />,
    ///     is forwarded through; empty when none routes it. The serializer gives a type one name, so the answer is kept per
    ///     type.
    /// </summary>
    public INotificationTransport[] TransportsFor(Type notificationType, string notificationName)
    {
        if (_all.Length == 0) return [];

        return _byNotificationType.GetOrAdd(notificationType, static (type, state) =>
        {
            List<INotificationTransport>? routing = null;
            foreach (var transport in state.All)
                if (transport.Routes(state.Name, type))
                    (routing ??= []).Add(transport);
            return routing?.ToArray() ?? [];
        }, (All: _all, Name: notificationName));
    }

    /// <summary>Whether a transport declares it forwards <paramref name="notificationType" /> by type (see <c>CQRCONF014</c>).</summary>
    public bool DeclaresPublished(Type notificationType) => _all.Length != 0 && _publishedTypes.Contains(notificationType);
}

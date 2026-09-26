using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     Everything one RabbitMQ transport runs on, one per transport and service provider: its settings, its connections
///     (one to publish on and one to consume on when it owns them, the application's one for both otherwise), what it
///     publishes where, and the state of its consumers. Disposed with the provider, which closes the connections it owns
///     after the hosted services, the outbox processor included, have stopped.
/// </summary>
internal sealed class RabbitMqTransportRuntime : IAsyncDisposable, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly Lazy<FrozenDictionary<string, ResolvedPublication>> _publications;
    private readonly ConcurrentDictionary<string, RabbitMqConsumerService> _consumers = new(StringComparer.Ordinal);
    private int _disposed;

    public RabbitMqTransportRuntime(string name, RabbitMqConnectionSource source, IServiceProvider services)
    {
        Name = name;
        _services = services;
        Options = services.GetRequiredService<IOptionsMonitor<RabbitMqTransportOptions>>().Get(name);
        TimeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var loggers = services.GetRequiredService<ILoggerFactory>();
        ConnectionLogger = loggers.CreateLogger<RabbitMqLink>();
        AppId = Options.AppId ?? DefaultAppId(services);
        var clientName = Options.ClientProvidedName ?? $"{AppId}/{name}";

        _publications = new Lazy<FrozenDictionary<string, ResolvedPublication>>(ResolvePublications);
        Publisher = new RabbitMqLink(source, services, name, "publish", clientName + "/publish", Options.ReconnectMaxDelay,
            DeclarePublisherTopologyAsync, ConnectionLogger, TimeProvider);
        // The application's connection is shared by both sides; the transport's own are two, since the broker throttles a
        // publishing connection under flow control, which would stall a consumer's acknowledgements on the same connection.
        Consumer = Options.Consumers.Count == 0 ? null
            : source.Owned
                ? new RabbitMqLink(source, services, name, "consume", clientName + "/consume", Options.ReconnectMaxDelay, null, ConnectionLogger, TimeProvider)
                : Publisher;
        Transport = new RabbitMqNotificationTransport(this, loggers.CreateLogger<RabbitMqNotificationTransport>());
    }

    /// <summary>The transport's name.</summary>
    public string Name { get; }

    /// <summary>The transport's settings.</summary>
    public RabbitMqTransportOptions Options { get; }

    /// <summary>The clock back-offs and holds wait on.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>The <c>app-id</c> of what the transport publishes.</summary>
    public string AppId { get; }

    /// <summary>Where the connections' life is logged.</summary>
    public ILogger ConnectionLogger { get; }

    /// <summary>The connection the transport publishes on.</summary>
    public RabbitMqLink Publisher { get; }

    /// <summary>The connection the transport consumes on, or <see langword="null" /> when it consumes nothing.</summary>
    public RabbitMqLink? Consumer { get; }

    /// <summary>The transport itself.</summary>
    public RabbitMqNotificationTransport Transport { get; }

    /// <summary>Why the publisher's exchanges could not be declared, while that is so.</summary>
    public string? PublisherTopologyFailure { get; private set; }

    /// <summary>The transport's consumers, by queue.</summary>
    public IReadOnlyCollection<RabbitMqConsumerService> Consumers => _consumers.Values.ToArray();

    /// <summary>What is published where, by notification name.</summary>
    public FrozenDictionary<string, ResolvedPublication> Publications => _publications.Value;

    /// <summary>Creates the consumer of <paramref name="queue" />, one of the queues the transport's options consume.</summary>
    public RabbitMqConsumerService CreateConsumer(string queue)
    {
        var definition = Options.Consumers.FirstOrDefault(c => c.Queue == queue)
                         ?? throw new InvalidOperationException($"RabbitMQ transport '{Name}' does not consume the queue '{queue}'.");
        return _consumers.GetOrAdd(queue, _ => new RabbitMqConsumerService(
            this,
            definition,
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetService<IOptions<OutboxProcessorOptions>>()?.Value ?? new OutboxProcessorOptions(),
            _services.GetRequiredService<ILoggerFactory>().CreateLogger<RabbitMqConsumerService>()));
    }

    /// <summary>
    ///     The routing keys of a consumer's bindings: a bound type's stable name, or the pattern as given. Fails, saying why,
    ///     when a bound type has no stable name (<c>CQRCONF014</c>).
    /// </summary>
    public bool TryResolveBindings(
        RabbitMqConsumerDefinition consumer,
        [NotNullWhen(true)] out IReadOnlyList<RabbitMqTopology.Binding>? bindings,
        [NotNullWhen(false)] out string? failure)
    {
        var serializer = _services.GetService<INotificationSerializer>();
        var resolved = new List<RabbitMqTopology.Binding>(consumer.Bindings.Count);
        foreach (var binding in consumer.Bindings)
        {
            var exchange = binding.Exchange ?? Options.DefaultExchange;
            if (binding.RoutingPattern is { } pattern)
            {
                resolved.Add(new RabbitMqTopology.Binding(exchange, pattern));
                continue;
            }

            var type = binding.NotificationType!;
            if (serializer is null || !serializer.TryGetNotificationName(type, out var name))
            {
                bindings = null;
                failure = $"Queue '{consumer.Queue}' is bound to notification '{type.FullName}', but the registered notification " +
                          "serializer gives it no name, so it can be neither bound nor read back (CQRCONF014). Mark it with [NotificationName].";
                return false;
            }

            resolved.Add(new RabbitMqTopology.Binding(exchange, name));
        }

        bindings = resolved;
        failure = null;
        return true;
    }

    /// <summary>
    ///     Whether an AMQP failure is the broker refusing what was asked (an entity that does not exist, a declaration that
    ///     differs from what exists, access denied) rather than the connection going away.
    /// </summary>
    public static bool IsRefusal(OperationInterruptedException exception, out string detail)
    {
        detail = exception.ShutdownReason is { } reason ? $"{reason.ReplyCode} {reason.ReplyText}" : exception.Message;
        return exception.ShutdownReason?.ReplyCode is 403 or 404 or 405 or 406;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await Transport.DisposeAsync().ConfigureAwait(false);
        if (Consumer is not null && !ReferenceEquals(Consumer, Publisher))
            await Consumer.DisposeAsync().ConfigureAwait(false);
        await Publisher.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Transport.Dispose();
        if (Consumer is not null && !ReferenceEquals(Consumer, Publisher))
            Consumer.Dispose();
        Publisher.Dispose();
    }

    // Every publication by the name it is stored under: those configured by name as they are, those configured by type
    // under the name the serializer gives the type. A type it does not name is left out (CQRCONF014 reports it, and a
    // publish of it fails before anything is stored).
    private FrozenDictionary<string, ResolvedPublication> ResolvePublications()
    {
        var serializer = _services.GetService<INotificationSerializer>();
        var resolved = new Dictionary<string, ResolvedPublication>(StringComparer.Ordinal);
        foreach (var (type, publication) in Options.PublicationsByType)
            if (serializer is not null && serializer.TryGetNotificationName(type, out var name))
                resolved[name] = Resolve(name, publication);
        foreach (var (name, publication) in Options.PublicationsByName)
            resolved[name] = Resolve(name, publication);
        return resolved.ToFrozenDictionary(StringComparer.Ordinal);

        ResolvedPublication Resolve(string name, RabbitMqPublicationDefinition publication)
            => new(publication.Exchange ?? Options.DefaultExchange, publication.RoutingKey ?? name, publication.AllowUnroutable);
    }

    // The publisher declares the exchanges it publishes to, on every connection it opens: a broker that restarted empty, or
    // an exchange someone deleted, is declared again. A declaration the broker refuses leaves the connection open (sends to
    // other exchanges still work) and is reported until one succeeds.
    private async Task DeclarePublisherTopologyAsync(IConnection connection, CancellationToken cancellationToken)
    {
        if (!Options.DeclareTopology) return;

        var exchanges = Publications.Values.Select(p => p.Exchange).Distinct(StringComparer.Ordinal).ToArray();
        if (exchanges.Length == 0) return;

        var description = $"the exchange(s) {string.Join(", ", exchanges.Select(e => $"'{e}'"))}";
        try
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (channel.ConfigureAwait(false))
                await RabbitMqTopology.DeclareExchangesAsync(channel, exchanges, cancellationToken).ConfigureAwait(false);
            PublisherTopologyFailure = null;
            RabbitMqLog.TopologyDeclared(ConnectionLogger, Name, description);
        }
        catch (OperationInterruptedException ex) when (connection.IsOpen && IsRefusal(ex, out var detail))
        {
            PublisherTopologyFailure = $"Declaring {description} failed: {detail}";
            RabbitMqLog.TopologyFailed(ConnectionLogger, ex, Name, description, detail);
        }
    }

    private static string DefaultAppId(IServiceProvider services)
        => services.GetService<IHostEnvironment>()?.ApplicationName is { Length: > 0 } application
            ? application
            : Assembly.GetEntryAssembly()?.GetName().Name ?? "cqrsharp";
}

/// <summary>Where the notifications stored under one name are published.</summary>
internal sealed record ResolvedPublication(string Exchange, string RoutingKey, bool AllowUnroutable);

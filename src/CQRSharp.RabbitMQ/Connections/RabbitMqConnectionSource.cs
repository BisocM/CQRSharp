using RabbitMQ.Client;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     Where a transport's connections come from: opened by the transport itself (from a URI or from the application's
///     <see cref="IConnectionFactory" />), which it then owns, reopens when lost and closes with the service provider; or an
///     <see cref="IConnection" /> the application owns, which the transport uses for publishing and consuming alike and never
///     closes.
/// </summary>
internal abstract class RabbitMqConnectionSource
{
    /// <summary>Whether the transport opens, reopens and closes the connections itself.</summary>
    public abstract bool Owned { get; }

    /// <summary>Opens a connection named <paramref name="clientProvidedName" />, or returns the application's.</summary>
    public abstract Task<IConnection> OpenAsync(IServiceProvider services, string clientProvidedName, CancellationToken cancellationToken);

    /// <summary>Connections the transport opens from <paramref name="uri" /> (<c>amqp://</c> or <c>amqps://</c>).</summary>
    /// <exception cref="ArgumentException">The URI is not an AMQP URI.</exception>
    public static RabbitMqConnectionSource FromUri(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("amqp" or "amqps"))
            throw new ArgumentException("The RabbitMQ connection URI must be an absolute amqp:// or amqps:// URI.", nameof(uri));

        return new FactorySource(_ => new ConnectionFactory
        {
            Uri = parsed,
            // The transport reopens a lost connection itself, and declares its topology and consumes again on the new one:
            // one mechanism, which also covers a consumer channel the broker closed without closing the connection.
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false
        });
    }

    /// <summary>Connections the transport opens from the factory the application supplies.</summary>
    public static RabbitMqConnectionSource FromFactory(Func<IServiceProvider, IConnectionFactory> factory, string registration)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new FactorySource(services => factory(services)
            ?? throw new InvalidOperationException($"The connection factory passed to {registration} returned null."));
    }

    /// <summary>The application's connection, used as given.</summary>
    public static RabbitMqConnectionSource Given(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new GivenSource(_ => connection);
    }

    /// <summary>The application's connection, from a factory that runs once per service provider.</summary>
    public static RabbitMqConnectionSource FromConnection(Func<IServiceProvider, IConnection> connection, string registration)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new GivenSource(services => connection(services)
            ?? throw new InvalidOperationException($"The connection factory passed to {registration} returned null."));
    }

    private sealed class FactorySource(Func<IServiceProvider, IConnectionFactory> factory) : RabbitMqConnectionSource
    {
        public override bool Owned => true;

        public override Task<IConnection> OpenAsync(IServiceProvider services, string clientProvidedName, CancellationToken cancellationToken)
            => factory(services).CreateConnectionAsync(clientProvidedName, cancellationToken);
    }

    private sealed class GivenSource(Func<IServiceProvider, IConnection> connection) : RabbitMqConnectionSource
    {
        public override bool Owned => false;

        public override Task<IConnection> OpenAsync(IServiceProvider services, string clientProvidedName, CancellationToken cancellationToken)
            => Task.FromResult(connection(services));
    }
}

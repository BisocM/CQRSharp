using CQRSharp.Pipelines;
using CQRSharp.RabbitMQ;
using RabbitMQ.Client;

// Namespace-extends the DI builder so the fluent verb reads naturally next to the rest of the app's wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     The RabbitMQ transport verb, used inside the builder's <c>UseOutbox(...)</c>: the notifications it is told to publish
///     leave the process through the outbox (stored with the request's data, sent at least once, in order per partition key,
///     confirmed by the broker), and the queues it consumes bring notifications into the outbox for the local handlers.
///     Several transports (several brokers) may be added, each under a name of its own.
/// </summary>
public static class RabbitMqOutboxBuilderExtensions
{
    /// <summary>
    ///     Adds a RabbitMQ transport on connections it opens from <paramref name="connectionUri" /> (<c>amqp://</c> or
    ///     <c>amqps://</c>), one to publish on and one to consume on, reopened whenever they are lost and closed with the
    ///     service provider.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="connectionUri">The broker's URI, such as <c>amqp://user:password@rabbit:5672/</c>.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionUri" /> is not an AMQP URI.</exception>
    public static OutboxStoreBuilder UseRabbitMq(this OutboxStoreBuilder builder, string connectionUri, Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Add(builder, RabbitMqConnectionSource.FromUri(connectionUri), configure);
    }

    /// <summary>
    ///     Adds a RabbitMQ transport on the application's <paramref name="connection" />, used for publishing and consuming
    ///     alike and never closed by CQRSharp. The connection should recover by itself (automatic recovery, as a connection
    ///     from a <see cref="ConnectionFactory" /> does by default); the transport waits for it while it is down.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="connection">The application's connection.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static OutboxStoreBuilder UseRabbitMq(this OutboxStoreBuilder builder, IConnection connection, Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Add(builder, RabbitMqConnectionSource.Given(connection), configure);
    }

    /// <summary>
    ///     Adds a RabbitMQ transport on the connection <paramref name="connectionFactory" /> returns, such as
    ///     <c>sp =&gt; sp.GetRequiredService&lt;IConnection&gt;()</c> for the connection Aspire's <c>AddRabbitMQClient</c>
    ///     registers. The factory runs once per service provider; the connection is used for publishing and consuming alike and
    ///     never closed by CQRSharp.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="connectionFactory">Returns the application's connection.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static OutboxStoreBuilder UseRabbitMq(
        this OutboxStoreBuilder builder,
        Func<IServiceProvider, IConnection> connectionFactory,
        Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Add(builder, RabbitMqConnectionSource.FromConnection(connectionFactory, nameof(UseRabbitMq)), configure);
    }

    /// <summary>
    ///     Adds a RabbitMQ transport on connections it opens from the <see cref="IConnectionFactory" />
    ///     <paramref name="factory" /> returns (the broker's address, credentials and TLS settings as the application configures
    ///     them), one to publish on and one to consume on, reopened whenever they are lost and closed with the service provider.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="factory">Returns the connection factory.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static OutboxStoreBuilder UseRabbitMq(
        this OutboxStoreBuilder builder,
        Func<IServiceProvider, IConnectionFactory> factory,
        Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Add(builder, RabbitMqConnectionSource.FromFactory(factory, nameof(UseRabbitMq)), configure);
    }

    private static OutboxStoreBuilder Add(OutboxStoreBuilder builder, RabbitMqConnectionSource source, Action<RabbitMqTransportBuilder>? configure)
    {
        var transport = new RabbitMqTransportBuilder();
        configure?.Invoke(transport);
        return builder.AddTransport(services => RabbitMqRegistration.Register(services, source, transport));
    }
}

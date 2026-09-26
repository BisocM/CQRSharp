using CQRSharp.RabbitMQ;
using RabbitMQ.Client;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registers a RabbitMQ transport outside the CQRSharp builder, for an application that wires its outbox with
///     <c>services</c> calls. The outbox must be on (<c>UseOutbox(...)</c>): a transport forwards what the outbox stores, and
///     host start fails without it (<c>CQRCONF013</c>). Prefer <c>UseOutbox(o =&gt; o.UseRabbitMq(...))</c>, which does both
///     in one place.
/// </summary>
public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    ///     Registers a RabbitMQ transport on connections it opens from <paramref name="connectionUri" />; see
    ///     <see cref="RabbitMqOutboxBuilderExtensions.UseRabbitMq(CQRSharp.Pipelines.OutboxStoreBuilder, string, Action{RabbitMqTransportBuilder})" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionUri">The broker's URI, such as <c>amqp://user:password@rabbit:5672/</c>.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionUri" /> is not an AMQP URI.</exception>
    public static IServiceCollection AddCqrsRabbitMq(this IServiceCollection services, string connectionUri, Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Add(services, RabbitMqConnectionSource.FromUri(connectionUri), configure);
    }

    /// <summary>
    ///     Registers a RabbitMQ transport on the application's <paramref name="connection" />; see
    ///     <see cref="RabbitMqOutboxBuilderExtensions.UseRabbitMq(CQRSharp.Pipelines.OutboxStoreBuilder, IConnection, Action{RabbitMqTransportBuilder})" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connection">The application's connection.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddCqrsRabbitMq(this IServiceCollection services, IConnection connection, Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Add(services, RabbitMqConnectionSource.Given(connection), configure);
    }

    /// <summary>
    ///     Registers a RabbitMQ transport on the connection <paramref name="connectionFactory" /> returns; see
    ///     <see cref="RabbitMqOutboxBuilderExtensions.UseRabbitMq(CQRSharp.Pipelines.OutboxStoreBuilder, Func{IServiceProvider, IConnection}, Action{RabbitMqTransportBuilder})" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionFactory">Returns the application's connection.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddCqrsRabbitMq(
        this IServiceCollection services,
        Func<IServiceProvider, IConnection> connectionFactory,
        Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Add(services, RabbitMqConnectionSource.FromConnection(connectionFactory, nameof(AddCqrsRabbitMq)), configure);
    }

    /// <summary>
    ///     Registers a RabbitMQ transport on connections it opens from the factory <paramref name="factory" /> returns; see
    ///     <see cref="RabbitMqOutboxBuilderExtensions.UseRabbitMq(CQRSharp.Pipelines.OutboxStoreBuilder, Func{IServiceProvider, IConnectionFactory}, Action{RabbitMqTransportBuilder})" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Returns the connection factory.</param>
    /// <param name="configure">What the transport publishes and consumes, and its settings.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddCqrsRabbitMq(
        this IServiceCollection services,
        Func<IServiceProvider, IConnectionFactory> factory,
        Action<RabbitMqTransportBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Add(services, RabbitMqConnectionSource.FromFactory(factory, nameof(AddCqrsRabbitMq)), configure);
    }

    private static IServiceCollection Add(IServiceCollection services, RabbitMqConnectionSource source, Action<RabbitMqTransportBuilder>? configure)
    {
        var transport = new RabbitMqTransportBuilder();
        configure?.Invoke(transport);
        RabbitMqRegistration.Register(services, source, transport);
        return services;
    }
}

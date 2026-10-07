using CQRSharp.RabbitMQ;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the CQRSharp RabbitMQ health check on an <see cref="IHealthChecksBuilder" />.</summary>
public static class RabbitMqHealthChecksBuilderExtensions
{
    /// <summary>
    ///     Registers a health check over every RabbitMQ transport of the application: the failure status while a connection is
    ///     not open, a publisher's exchanges cannot be declared, or a consumer's topology or subscription is refused; degraded
    ///     while the broker blocks a connection (a resource alarm) or a consumer is between channels; healthy otherwise. The
    ///     state of each connection and queue is in the result's data.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name of the check.</param>
    /// <param name="failureStatus">The status reported for a failure; <see cref="HealthStatus.Unhealthy" /> when not set.</param>
    /// <param name="tags">Tags to filter the check by.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHealthChecksBuilder AddCqrsRabbitMq(
        this IHealthChecksBuilder builder,
        string name = "cqrsharp.rabbitmq",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.Add(new HealthCheckRegistration(
            name,
            provider => new CqrsRabbitMqHealthCheck(provider.GetServices<RabbitMqTransportRuntime>()),
            failureStatus ?? HealthStatus.Unhealthy,
            tags));
    }
}

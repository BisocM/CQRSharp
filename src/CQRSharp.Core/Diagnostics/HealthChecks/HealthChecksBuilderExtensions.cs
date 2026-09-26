using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CQRSharp.Core.Diagnostics.HealthChecks;

/// <summary>
///     Provides extension methods for registering CQRSharp health checks on an
///     <see cref="IHealthChecksBuilder" />.
/// </summary>
public static class HealthChecksBuilderExtensions
{
    /// <summary>
    ///     Registers the <see cref="CqrsOutboxHealthCheck" />, which measures the outbox backlog on every probe and
    ///     reports degraded when the oldest undelivered message is older than <see cref="OutboxHealthCheckOptions.MaxLag" />
    ///     or there are more dead letters than <see cref="OutboxHealthCheckOptions.MaxDeadLetters" />, and the failure
    ///     status when the store cannot be read. The thresholds belong to this registration: they are the
    ///     <see cref="OutboxHealthCheckOptions" /> named <paramref name="name" />, so two registrations under different
    ///     names (a liveness and a readiness check, say) keep their own, and can also be bound from configuration with
    ///     <c>services.Configure&lt;OutboxHealthCheckOptions&gt;(name, section)</c>.
    /// </summary>
    /// <param name="builder">The health checks builder to add the check to.</param>
    /// <param name="name">The name used to identify the health check.</param>
    /// <param name="failureStatus">
    ///     The status reported when the store cannot be read; defaults to <see cref="HealthStatus.Unhealthy" />.
    /// </param>
    /// <param name="tags">An optional set of tags used to filter the health check.</param>
    /// <param name="configure">Adjusts this registration's thresholds.</param>
    /// <returns>The same <see cref="IHealthChecksBuilder" /> instance so that calls can be chained.</returns>
    public static IHealthChecksBuilder AddCqrsOutbox(
        this IHealthChecksBuilder builder,
        string name = "cqrsharp.outbox",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<OutboxHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        tags ??= Array.Empty<string>();

        var thresholds = builder.Services.AddOptions<OutboxHealthCheckOptions>(name)
            .Validate(o => o.MaxLag is null || o.MaxLag >= TimeSpan.Zero, $"OutboxHealthCheckOptions.MaxLag of the '{name}' check must not be negative.")
            .Validate(o => o.MaxDeadLetters is null || o.MaxDeadLetters >= 0, $"OutboxHealthCheckOptions.MaxDeadLetters of the '{name}' check must not be negative.")
            .ValidateOnStart();
        if (configure is not null)
            thresholds.Configure(configure);

        return builder.AddCheck<CqrsOutboxHealthCheck>(
            name,
            failureStatus ?? HealthStatus.Unhealthy,
            tags);
    }
}

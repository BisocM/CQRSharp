using System.Collections.Generic;
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
    ///     Registers the <see cref="CqrsBindingsHealthCheck" /> that validates all CQRSharp request bindings.
    /// </summary>
    /// <param name="builder">The health checks builder to add the check to.</param>
    /// <param name="name">The name used to identify the health check.</param>
    /// <param name="failureStatus">
    ///     The status reported when the check fails; defaults to <see cref="HealthStatus.Unhealthy" /> when not specified.
    /// </param>
    /// <param name="tags">An optional set of tags used to filter the health check.</param>
    /// <returns>The same <see cref="IHealthChecksBuilder" /> instance so that calls can be chained.</returns>
    public static IHealthChecksBuilder AddCqrsBindings(
        this IHealthChecksBuilder builder,
        string name = "cqrsharp.bindings",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        tags ??= Array.Empty<string>();
        return builder.AddCheck<CqrsBindingsHealthCheck>(
            name,
            failureStatus ?? HealthStatus.Unhealthy,
            tags);
    }
}

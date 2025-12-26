using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CQRSharp.Core.Diagnostics.HealthChecks;

public static class HealthChecksBuilderExtensions
{
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

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CQRSharp.Core.Diagnostics.HealthChecks;

public sealed class CqrsBindingsHealthCheck(ICqrsDiagnostics diagnostics) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var bindings = diagnostics.DescribeAllRequests();

        var errorCount = 0;
        var warningCount = 0;

        foreach (var binding in bindings)
        {
            foreach (var issue in binding.Issues)
            {
                switch (issue.Severity)
                {
                    case CqrsBindingIssueSeverity.Error:
                        errorCount++;
                        break;
                    case CqrsBindingIssueSeverity.Warning:
                        warningCount++;
                        break;
                }
            }
        }

        var data = new Dictionary<string, object>
        {
            ["requestCount"] = bindings.Count,
            ["errorCount"] = errorCount,
            ["warningCount"] = warningCount
        };

        if (errorCount > 0)
        {
            data["errors"] = bindings
                .Where(b => b.Issues.Any(i => i.Severity == CqrsBindingIssueSeverity.Error))
                .Select(b => $"{b.RequestType.FullName}: {string.Join("; ", b.Issues.Where(i => i.Severity == CqrsBindingIssueSeverity.Error).Select(i => i.Message))}")
                .Take(20)
                .ToArray();

            return Task.FromResult(HealthCheckResult.Unhealthy(
                description: $"CQRSharp request bindings have {errorCount} error(s).",
                data: data));
        }

        if (warningCount > 0)
        {
            data["warnings"] = bindings
                .Where(b => b.Issues.Any(i => i.Severity == CqrsBindingIssueSeverity.Warning))
                .Select(b => $"{b.RequestType.FullName}: {string.Join("; ", b.Issues.Where(i => i.Severity == CqrsBindingIssueSeverity.Warning).Select(i => i.Message))}")
                .Take(20)
                .ToArray();

            return Task.FromResult(HealthCheckResult.Degraded(
                description: $"CQRSharp request bindings have {warningCount} warning(s).",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            description: $"CQRSharp request bindings OK ({bindings.Count} request(s)).",
            data: data));
    }
}

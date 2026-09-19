using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CQRSharp.Core.Diagnostics.HealthChecks;

/// <summary>
///     A health check that inspects every registered CQRSharp request binding and reports
///     unhealthy when any binding has errors, degraded when any has warnings, and healthy otherwise.
/// </summary>
public sealed class CqrsBindingsHealthCheck(ICqrsDiagnostics diagnostics) : IHealthCheck
{
    /// <summary>
    ///     Evaluates all request bindings, aggregating their error and warning issues into a
    ///     <see cref="HealthCheckResult" /> whose status and data reflect the worst severity found.
    /// </summary>
    /// <param name="context">The context under which the health check is being run.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A completed task containing an unhealthy result when any binding has errors, a degraded
    ///     result when any has warnings, or a healthy result when all bindings are valid.
    /// </returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var bindings = diagnostics.DescribeAllRequests();

        var errorCount = 0;
        var warningCount = 0;

        foreach (var binding in bindings)
        foreach (var issue in binding.Issues)
            switch (issue.Severity)
            {
                case CqrsBindingIssueSeverity.Error:
                    errorCount++;
                    break;
                case CqrsBindingIssueSeverity.Warning:
                    warningCount++;
                    break;
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
                $"CQRSharp request bindings have {errorCount} error(s).",
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
                $"CQRSharp request bindings have {warningCount} warning(s).",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"CQRSharp request bindings OK ({bindings.Count} request(s)).",
            data));
    }
}
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     A hosted service that validates CQRSharp configuration and request bindings once at host start, turning the
///     framework's previously silent fallbacks into loud, early failures. It aggregates the global configuration
///     issues with every per-request binding issue, logs each one with its stable code, and — depending on the
///     configured <see cref="CqrsValidationPolicy" /> — aborts host start by throwing when errors (or warnings) are
///     present. AOT-clean: resolves services through the container and inspects issue records only.
/// </summary>
internal sealed class CqrsStartupValidator(
    IServiceScopeFactory scopeFactory,
    IOptions<CqrsStartupValidationOptions> options,
    ILogger<CqrsStartupValidator> logger) : IHostedService
{
    private readonly CqrsStartupValidationOptions _options = options.Value;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var policy = _options.Policy;
        if (policy == CqrsValidationPolicy.Off) return;

        // ICqrsDiagnostics is scoped; create a scope to resolve it and the bindings it describes.
        await using var scope = scopeFactory.CreateAsyncScope();
        var diagnostics = scope.ServiceProvider.GetService<ICqrsDiagnostics>();

        List<CqrsBindingIssue> issues;
        if (diagnostics is null)
        {
            // No generated diagnostics means AddCqrsGenerated() was never applied: report it as the same misordering
            // error the inspector would, since the inspection itself cannot run.
            issues = new List<CqrsBindingIssue>
            {
                new(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF004",
                    "The CQRSharp diagnostics service (ICqrsDiagnostics) is not registered, so startup validation cannot " +
                    "run. The source-generated registrations were not applied: call AddCqrsGenerated(...) instead of AddCqrs(...).")
            };
        }
        else
        {
            issues = new List<CqrsBindingIssue>(diagnostics.DescribeConfiguration());
            issues.AddRange(diagnostics.DescribeAllRequests().SelectMany(b => b.Issues));
        }

        var errorCount = 0;
        var warningCount = 0;

        foreach (var issue in issues)
            switch (issue.Severity)
            {
                case CqrsBindingIssueSeverity.Error:
                    errorCount++;
                    logger.LogError("CQRSharp startup validation error {Code}: {Message}", issue.Code, issue.Message);
                    break;
                case CqrsBindingIssueSeverity.Warning:
                    warningCount++;
                    logger.LogWarning("CQRSharp startup validation warning {Code}: {Message}", issue.Code, issue.Message);
                    break;
                default:
                    logger.LogInformation("CQRSharp startup validation note {Code}: {Message}", issue.Code, issue.Message);
                    break;
            }

        if (errorCount == 0 && warningCount == 0)
        {
            logger.LogInformation("CQRSharp startup validation passed with no issues.");
            return;
        }

        logger.LogInformation(
            "CQRSharp startup validation found {ErrorCount} error(s) and {WarningCount} warning(s) (policy: {Policy}).",
            errorCount, warningCount, policy);

        var abortOnError = policy is CqrsValidationPolicy.ThrowOnError or CqrsValidationPolicy.ThrowOnWarning && errorCount > 0;
        var abortOnWarning = policy == CqrsValidationPolicy.ThrowOnWarning && warningCount > 0;

        if (!abortOnError && !abortOnWarning) return;

        var severitiesToThrow = policy == CqrsValidationPolicy.ThrowOnWarning
            ? new[] { CqrsBindingIssueSeverity.Error, CqrsBindingIssueSeverity.Warning }
            : new[] { CqrsBindingIssueSeverity.Error };

        var reported = issues
            .Where(i => Array.IndexOf(severitiesToThrow, i.Severity) >= 0)
            .Select(i => $"  [{i.Severity}] {i.Code}: {i.Message}");

        throw new InvalidOperationException(
            "CQRSharp startup validation failed; aborting host start:" + Environment.NewLine +
            string.Join(Environment.NewLine, reported));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

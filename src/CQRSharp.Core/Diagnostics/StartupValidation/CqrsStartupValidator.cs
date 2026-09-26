using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Validates CQRSharp's configuration and every request binding once, when the host starts, turning what would
///     otherwise fail silently or at the first dispatch into a report at startup: each issue is logged with its stable
///     code, and, as the configured <see cref="CqrsValidationPolicy" /> says, host start is aborted when errors (or
///     warnings) are present.
/// </summary>
/// <remarks>
///     <para>
///         Off outside the Development environment unless a policy is set: it resolves every pipeline behavior of every
///         request, the cold cost each request type otherwise pays at its first dispatch, all at once before the host
///         serves anything. In Development it is on unless a policy is set, as the host's own container validation is.
///         The rules that have a point of first use (<see cref="CqrsConfigurationRules" />) fail or warn at that point in
///         every environment, validated at startup or not.
///     </para>
///     <para>
///         The validation runs in <see cref="StartingAsync" />, which the host calls on every lifecycle service before it
///         starts any hosted service, also when services start concurrently. So nothing, neither an application service
///         that dispatches while it starts (a seeder, a migrator) nor the web server, runs against a configuration the
///         validator is about to reject, whatever order the services were registered in.
///     </para>
/// </remarks>
internal sealed partial class CqrsStartupValidator(
    IServiceScopeFactory scopeFactory,
    IOptions<CqrsStartupValidationOptions> options,
    ILogger<CqrsStartupValidator> logger,
    IHostEnvironment? environment = null) : IHostedLifecycleService
{
    /// <summary>
    ///     The policy in effect: the one set, or, when none is, <see cref="CqrsValidationPolicy.ThrowOnError" /> in the
    ///     Development environment and <see cref="CqrsValidationPolicy.Off" /> in any other or without a host environment.
    /// </summary>
    internal static CqrsValidationPolicy EffectivePolicy(CqrsValidationPolicy? configured, IHostEnvironment? environment)
        => configured ?? (environment?.IsDevelopment() == true ? CqrsValidationPolicy.ThrowOnError : CqrsValidationPolicy.Off);

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var policy = EffectivePolicy(options.Value.Policy, environment);
        if (policy == CqrsValidationPolicy.Off) return;

        // ICqrsDiagnostics is scoped: resolve it, and everything it describes, from a scope of its own.
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
            Report(policy, Collect(scope.ServiceProvider));
    }

    private static List<CqrsBindingIssue> Collect(IServiceProvider services)
    {
        if (services.GetService<ICqrsDiagnostics>() is not { } diagnostics)
            return
            [
                new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF004",
                    "The CQRSharp diagnostics service (ICqrsDiagnostics) is not registered, so startup validation cannot " +
                    "run. The source-generated registrations were not applied: call AddCqrsGenerated() (or AddCqrsGenerated(b => ...)) instead of AddCqrs().")
            ];

        // Every request is described once: the configuration checks reuse the descriptions, which resolve every
        // pipeline behavior of every request.
        var bindings = diagnostics.DescribeAllRequests();
        var issues = new List<CqrsBindingIssue>(diagnostics is CqrsDiagnostics built
            ? built.DescribeConfiguration(bindings)
            : diagnostics.DescribeConfiguration());
        foreach (var binding in bindings)
            issues.AddRange(binding.Issues);
        return issues;
    }

    private void Report(CqrsValidationPolicy policy, List<CqrsBindingIssue> issues)
    {
        var errorCount = 0;
        var warningCount = 0;
        foreach (var issue in issues)
            if (issue.Severity == CqrsBindingIssueSeverity.Error)
            {
                errorCount++;
                LogIssueError(logger, issue.Code, issue.Message);
            }
            else
            {
                warningCount++;
                LogIssueWarning(logger, issue.Code, issue.Message);
            }

        if (errorCount == 0 && warningCount == 0)
        {
            LogPassed(logger);
            return;
        }

        LogSummary(logger, errorCount, warningCount, policy);

        var abortOnWarning = policy == CqrsValidationPolicy.ThrowOnWarning;
        var abort = (errorCount > 0 && policy is CqrsValidationPolicy.ThrowOnError or CqrsValidationPolicy.ThrowOnWarning) ||
                    (warningCount > 0 && abortOnWarning);
        if (!abort) return;

        var reported = issues
            .Where(i => i.Severity == CqrsBindingIssueSeverity.Error || abortOnWarning)
            .Select(i => $"  [{i.Severity}] {i.Code}: {i.Message}");

        throw new InvalidOperationException(
            "CQRSharp startup validation failed; aborting host start:" + Environment.NewLine +
            string.Join(Environment.NewLine, reported));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(1200, LogLevel.Error, "CQRSharp startup validation error {Code}: {Message}")]
    private static partial void LogIssueError(ILogger logger, string code, string message);

    [LoggerMessage(1201, LogLevel.Warning, "CQRSharp startup validation warning {Code}: {Message}")]
    private static partial void LogIssueWarning(ILogger logger, string code, string message);

    [LoggerMessage(1202, LogLevel.Information, "CQRSharp startup validation passed with no issues.")]
    private static partial void LogPassed(ILogger logger);

    [LoggerMessage(1203, LogLevel.Information, "CQRSharp startup validation found {ErrorCount} error(s) and {WarningCount} warning(s) (policy: {Policy}).")]
    private static partial void LogSummary(ILogger logger, int errorCount, int warningCount, CqrsValidationPolicy policy);
}

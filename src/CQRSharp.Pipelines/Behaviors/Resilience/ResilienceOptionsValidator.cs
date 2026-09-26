using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Rejects a <see cref="ResilienceOptions" /> the retry schedule cannot honour, at host start rather than on the first
///     retry. Registered once however often resilience is enabled, so a failure is reported once.
/// </summary>
internal sealed class ResilienceOptionsValidator : IValidateOptions<ResilienceOptions>
{
    public ValidateOptionsResult Validate(string? name, ResilienceOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        var failures = new List<string>();
        if (options.MaxRetries < 0)
            failures.Add("ResilienceOptions.MaxRetries must not be negative.");
        if (options.BaseDelay < TimeSpan.Zero)
            failures.Add("ResilienceOptions.BaseDelay must not be negative.");
        if (options.BaseDelay > TimerLimits.MaxDelay)
            failures.Add("ResilienceOptions.BaseDelay is longer than a timer can wait.");
        if (options.MaxDelay < options.BaseDelay)
            failures.Add("ResilienceOptions.MaxDelay must be at least BaseDelay.");
        if (options.MaxDelay > TimerLimits.MaxDelay)
            failures.Add("ResilienceOptions.MaxDelay is longer than a timer can wait.");
        if (!double.IsFinite(options.BackoffMultiplier) || options.BackoffMultiplier < 1)
            failures.Add("ResilienceOptions.BackoffMultiplier must be a finite number of at least 1.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

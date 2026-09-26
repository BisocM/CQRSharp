using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Rejects a <see cref="RateLimitingOptions" /> the limiter cannot run with, at host start rather than when the limiter
///     is first resolved. Registered once however often rate limiting is enabled, so a failure is reported once.
/// </summary>
internal sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        var failures = new List<string>();
        if (options.MaxTokens <= 0)
            failures.Add("RateLimitingOptions.MaxTokens must be greater than zero.");
        if (!double.IsFinite(options.ReplenishRatePerSecond) || options.ReplenishRatePerSecond <= 0)
            failures.Add("RateLimitingOptions.ReplenishRatePerSecond must be a finite number greater than zero.");
        if (options.MaxEntries <= 0)
            failures.Add("RateLimitingOptions.MaxEntries must be greater than zero.");
        if (options.MaxIdleTime < TimeSpan.Zero)
            failures.Add("RateLimitingOptions.MaxIdleTime must not be negative.");
        if (options.CleanupInterval < TimeSpan.Zero)
            failures.Add("RateLimitingOptions.CleanupInterval must not be negative.");
        if (options.CleanupInterval > TimerLimits.MaxDelay)
            failures.Add("RateLimitingOptions.CleanupInterval is longer than a timer can wait.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

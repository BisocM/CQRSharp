using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The value rules of <see cref="EfCoreOutboxStoreOptions" />, checked at host start. A class registered once
///     (<c>TryAddEnumerable</c>), so a store verb that runs twice does not report each failure twice.
/// </summary>
internal sealed class EfCoreOutboxStoreOptionsValidator : IValidateOptions<EfCoreOutboxStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, EfCoreOutboxStoreOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        var failures = new List<string>();
        if (!OptionLimits.IsDuration(options.VisibilityTimeout)) failures.Add("VisibilityTimeout must be greater than zero and must not exceed 10 years.");
        if (options.MaxClaimAttempts < 1) failures.Add("MaxClaimAttempts must be at least 1.");
        if (options.DeadLetterRetention is { } deadLetters && !OptionLimits.IsDuration(deadLetters))
            failures.Add("DeadLetterRetention must be greater than zero and must not exceed 10 years when set.");
        if (!OptionLimits.IsDuration(options.InboxRetention)) failures.Add("InboxRetention must be greater than zero and must not exceed 10 years.");
        if (options.ProcessedRetention is { } processed && !OptionLimits.IsDuration(processed))
            failures.Add("ProcessedRetention must be greater than zero and must not exceed 10 years when set.");
        if (options.PurgeInterval <= TimeSpan.Zero || options.PurgeInterval > OptionLimits.LongestTimerDelay)
            failures.Add("PurgeInterval must be greater than zero and no longer than a timer can wait (about 49 days).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

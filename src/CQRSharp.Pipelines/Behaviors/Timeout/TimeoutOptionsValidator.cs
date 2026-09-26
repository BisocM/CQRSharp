using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Rejects a <see cref="TimeoutOptions" /> no timer can enforce, at host start. Registered once however often the
///     timeout is enabled, so a failure is reported once.
/// </summary>
internal sealed class TimeoutOptionsValidator : IValidateOptions<TimeoutOptions>
{
    public ValidateOptionsResult Validate(string? name, TimeoutOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        if (options.Timeout <= TimeSpan.Zero)
            return ValidateOptionsResult.Fail("TimeoutOptions.Timeout must be greater than zero.");
        if (options.Timeout > TimerLimits.MaxDelay)
            return ValidateOptionsResult.Fail("TimeoutOptions.Timeout is longer than a timer can wait.");

        return ValidateOptionsResult.Success;
    }
}

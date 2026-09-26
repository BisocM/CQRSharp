using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The value rules of <see cref="EfCoreIdempotencyStoreOptions" />, checked at host start. A class registered once
///     (<c>TryAddEnumerable</c>), so a store verb that runs twice does not report each failure twice.
/// </summary>
internal sealed class EfCoreIdempotencyStoreOptionsValidator : IValidateOptions<EfCoreIdempotencyStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, EfCoreIdempotencyStoreOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        return OptionLimits.IsDuration(options.Retention)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Retention must be greater than zero and must not exceed 10 years.");
    }
}

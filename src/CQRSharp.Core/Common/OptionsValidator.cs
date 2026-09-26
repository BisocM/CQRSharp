using Microsoft.Extensions.Options;

namespace CQRSharp.Core;

/// <summary>
///     The validation of one options type as a class, so it is registered once (<c>TryAddEnumerable</c>) however often the
///     registration that adds it runs: every <c>AddCqrsGenerated</c> call runs <c>AddCqrs</c> again, and a validator
///     added with <c>OptionsBuilder.Validate</c> would be added again with it, reporting each failure once per call.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
internal abstract class OptionsValidator<TOptions> : IValidateOptions<TOptions> where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        var failures = Failures(options).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>What is wrong with <paramref name="options" />, one message per rule it breaks.</summary>
    protected abstract IEnumerable<string> Failures(TOptions options);
}

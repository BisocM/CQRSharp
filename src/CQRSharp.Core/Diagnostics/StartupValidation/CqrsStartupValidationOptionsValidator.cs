namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Rejects a <see cref="CqrsStartupValidationOptions" /> whose policy is set to a value that is not defined, at host
///     start: it would otherwise validate and never abort, acting as <see cref="CqrsValidationPolicy.WarnOnly" />.
/// </summary>
internal sealed class CqrsStartupValidationOptionsValidator : OptionsValidator<CqrsStartupValidationOptions>
{
    protected override IEnumerable<string> Failures(CqrsStartupValidationOptions options)
    {
        if (options.Policy is { } policy && !Enum.IsDefined(policy))
            yield return "CqrsStartupValidationOptions.Policy must be a defined CqrsValidationPolicy.";
    }
}

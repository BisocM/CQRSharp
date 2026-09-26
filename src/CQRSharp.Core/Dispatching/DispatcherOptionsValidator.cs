namespace CQRSharp.Core;

/// <summary>Rejects a <see cref="DispatcherOptions" /> with a mode that is not defined (a number bound from configuration), at host start.</summary>
internal sealed class DispatcherOptionsValidator : OptionsValidator<DispatcherOptions>
{
    protected override IEnumerable<string> Failures(DispatcherOptions options)
    {
        if (!Enum.IsDefined(options.RunMode))
            yield return "DispatcherOptions.RunMode must be a defined RunMode.";
        if (!Enum.IsDefined(options.ScopeMode))
            yield return "DispatcherOptions.ScopeMode must be a defined ExecutionScopeMode.";
    }
}

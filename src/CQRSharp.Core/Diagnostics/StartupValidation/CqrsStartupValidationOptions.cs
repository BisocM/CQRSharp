
namespace CQRSharp;

/// <summary>
///     Configures the fail-fast startup validator that inspects CQRSharp configuration and request bindings when
///     the host starts.
/// </summary>
public sealed class CqrsStartupValidationOptions
{
    /// <summary>
    ///     How the validator reacts to the issues it finds. Defaults to <see cref="CqrsValidationPolicy.Off" /> — the
    ///     validator is opt-in, through <c>ValidateOnStart()</c> on the builder or this option; <c>ThrowOnError</c>
    ///     aborts host start when any error is found while leaving warnings non-fatal.
    /// </summary>
    public CqrsValidationPolicy Policy { get; set; } = CqrsValidationPolicy.Off;
}

using CQRSharp.Core.Options.Enums;

namespace CQRSharp.Core.Options;

/// <summary>
///     Configures the fail-fast startup validator that inspects CQRSharp configuration and request bindings when
///     the host starts.
/// </summary>
public sealed class CqrsStartupValidationOptions
{
    /// <summary>
    ///     How the validator reacts to the issues it finds. Defaults to <see cref="CqrsValidationPolicy.ThrowOnError" />,
    ///     which aborts host start when any error is found while leaving warnings non-fatal.
    /// </summary>
    public CqrsValidationPolicy Policy { get; set; } = CqrsValidationPolicy.ThrowOnError;
}

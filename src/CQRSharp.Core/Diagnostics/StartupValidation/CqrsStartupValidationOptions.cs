namespace CQRSharp;

/// <summary>
///     Configures the fail-fast startup validator that inspects CQRSharp configuration and request bindings when
///     the host starts.
/// </summary>
public sealed class CqrsStartupValidationOptions
{
    /// <summary>
    ///     How the validator reacts to the issues it finds, or <see langword="null" /> (the default) to leave it to the
    ///     host environment: <see cref="CqrsValidationPolicy.ThrowOnError" /> when the host's <c>IHostEnvironment</c> is
    ///     Development, the way the host validates the container there (<c>ValidateOnBuild</c>, <c>ValidateScopes</c>),
    ///     and <see cref="CqrsValidationPolicy.Off" /> in any other environment or without a host. A value set here, by
    ///     <c>ValidateOnStart(...)</c> on the builder (<c>ValidateOnStart(false)</c> included) or by configuration, applies
    ///     in every environment.
    /// </summary>
    public CqrsValidationPolicy? Policy { get; set; }
}

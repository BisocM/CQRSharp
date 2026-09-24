namespace CQRSharp;

/// <summary>
///     Controls how the startup validator reacts to the configuration and binding issues it finds when the host
///     starts. The validator runs once, before any hosted service starts (and so before requests are served).
/// </summary>
public enum CqrsValidationPolicy
{
    /// <summary>
    ///     Validation is skipped entirely; no scope is created and nothing is logged or thrown.
    /// </summary>
    Off,

    /// <summary>
    ///     Every error and warning is logged, but host start always proceeds; nothing is thrown.
    /// </summary>
    WarnOnly,

    /// <summary>
    ///     Errors and warnings are logged, and host start is aborted (an exception is thrown) when any error is
    ///     found; warnings alone do not abort. This is what <c>ValidateOnStart()</c> selects.
    /// </summary>
    ThrowOnError,

    /// <summary>
    ///     Errors and warnings are logged, and host start is aborted when any error <em>or</em> warning is found.
    /// </summary>
    ThrowOnWarning
}

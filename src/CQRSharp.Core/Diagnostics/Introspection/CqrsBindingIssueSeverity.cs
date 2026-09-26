namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Indicates how serious a <see cref="CqrsBindingIssue" /> is.
/// </summary>
public enum CqrsBindingIssueSeverity
{
    /// <summary>
    ///     The binding works but may behave unexpectedly and should be reviewed. The startup validator aborts host start
    ///     on a warning only under <see cref="CqrsValidationPolicy.ThrowOnWarning" />.
    /// </summary>
    Warning = 1,

    /// <summary>
    ///     The configuration or the binding is broken: something will fail, or silently not happen, at runtime.
    /// </summary>
    Error = 2
}

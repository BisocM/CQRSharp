namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Indicates how serious a <see cref="CqrsBindingIssue" /> is.
/// </summary>
public enum CqrsBindingIssueSeverity
{
    /// <summary>
    ///     Informational only; the binding is valid and requires no action.
    /// </summary>
    Info = 0,

    /// <summary>
    ///     The binding works but may behave unexpectedly and should be reviewed.
    /// </summary>
    Warning = 1,

    /// <summary>
    ///     The binding is invalid and cannot be used as described.
    /// </summary>
    Error = 2
}
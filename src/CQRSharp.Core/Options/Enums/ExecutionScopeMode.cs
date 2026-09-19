namespace CQRSharp.Core.Options.Enums;

/// <summary>
///     Controls which dependency injection scope is used to execute requests and notifications.
/// </summary>
public enum ExecutionScopeMode
{
    /// <summary>
    ///     Execute within the current dependency injection scope (MediatR-like behavior).
    /// </summary>
    Current,

    /// <summary>
    ///     Create a new child scope for each execution.
    /// </summary>
    New
}
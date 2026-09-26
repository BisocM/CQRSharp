namespace CQRSharp;

/// <summary>
///     Controls which dependency injection scope a sent request or an enumerated stream executes in. Notifications are
///     not affected: their handlers and behaviors are resolved from the scope they are published from.
/// </summary>
/// <remarks>
///     The scope a request executes in is where its handler, its behaviors and their dependencies are resolved. Its
///     context is not: the context factory is resolved from the caller's scope (the scope of the dispatcher the request
///     is sent through) in either mode, so a factory that reads the caller from a scoped service sees the caller's
///     instance, not a fresh one of the new scope.
/// </remarks>
public enum ExecutionScopeMode
{
    /// <summary>
    ///     Execute in the scope of the dispatcher the request is sent through, so nested requests share its scoped services
    ///     (the default).
    /// </summary>
    Current,

    /// <summary>
    ///     Create a new child scope for each execution: each sent request, and each enumeration of a stream, which keeps
    ///     the scope until its enumeration ends.
    /// </summary>
    New
}

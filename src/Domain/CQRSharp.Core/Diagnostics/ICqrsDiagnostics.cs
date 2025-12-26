namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Provides runtime diagnostics and introspection for CQRSharp request bindings.
/// </summary>
public interface ICqrsDiagnostics
{
    /// <summary>
    ///     Attempts to describe the handler and pipeline bound to a request type in the current scope.
    /// </summary>
    bool TryDescribeRequest(Type requestType, out CqrsRequestBinding binding);

    /// <summary>
    ///     Describes the handler and pipeline bound to a request type in the current scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the request type is unknown to the diagnostics system.</exception>
    CqrsRequestBinding DescribeRequest(Type requestType);

    /// <summary>
    ///     Describes all request bindings known to the diagnostics system in the current scope.
    /// </summary>
    IReadOnlyList<CqrsRequestBinding> DescribeAllRequests();
}


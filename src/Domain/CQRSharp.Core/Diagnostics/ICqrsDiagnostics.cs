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

    /// <summary>
    ///     Inspects the resolved CQRSharp services and options for global, non-per-request misconfigurations —
    ///     an unbacked outbox mode, a transactional outbox that cannot detect a transaction, notifications that
    ///     bypass the outbox, or a missing generated registry — returning one issue per problem (empty when clean).
    ///     The only implementer is the source-generated diagnostics class, re-emitted in lockstep with this method.
    /// </summary>
    IReadOnlyList<CqrsBindingIssue> DescribeConfiguration();
}
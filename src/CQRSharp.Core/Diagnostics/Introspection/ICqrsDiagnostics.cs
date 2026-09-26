using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Describes how the application's requests are wired and what is wrong with its configuration: the same checks the
///     startup validator (<c>ValidateOnStart()</c>) runs, available on demand. Resolve it from a DI scope; a description
///     reflects what that scope resolves.
/// </summary>
public interface ICqrsDiagnostics
{
    /// <summary>
    ///     Describes the handler, context, interceptors and pipeline bound to a request type in the current scope.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <param name="binding">The description, when the application dispatches the request type.</param>
    /// <returns><see langword="true" /> when the application dispatches the request type.</returns>
    bool TryDescribeRequest(Type requestType, [NotNullWhen(true)] out CqrsRequestBinding? binding);

    /// <summary>
    ///     Describes the handler, context, interceptors and pipeline bound to a request type in the current scope.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <returns>The description.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the application does not dispatch the request type.</exception>
    CqrsRequestBinding DescribeRequest(Type requestType);

    /// <summary>
    ///     Describes every request type the application dispatches, ordered by type name: the requests of every
    ///     source-generated module, including closed generic requests and requests declared in assemblies the generator
    ///     does not run in.
    /// </summary>
    /// <returns>One description per request type.</returns>
    IReadOnlyList<CqrsRequestBinding> DescribeAllRequests();

    /// <summary>
    ///     Inspects the resolved CQRSharp services and options for misconfigurations that are not about one request's
    ///     binding (an outbox mode without its store, serializer or subscriptions, a transactional outbox without a unit of
    ///     work, notifications with handlers that bypass the outbox, an idempotency or retry marker without its behavior,
    ///     clashing handler or notification names, …), returning one <c>CQRCONF</c> issue per problem; empty when the
    ///     configuration is clean.
    /// </summary>
    /// <returns>The configuration issues.</returns>
    IReadOnlyList<CqrsBindingIssue> DescribeConfiguration();
}

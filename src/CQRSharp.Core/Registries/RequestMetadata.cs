using System.ComponentModel;

namespace CQRSharp.Core.Registries;

/// <summary>
///     The compile-time registration record of one request type, as the source generator emits it into a module keyed
///     by the request type: the handler that serves it, its context type, and the interceptor and exemption attributes
///     declared on it. The
///     executor plans the request's dispatch from it once per service provider; it is never written onto a request.
///     <c>ICqrsDiagnostics.DescribeRequest</c> is the supported view of how a request is wired.
/// </summary>
/// <param name="HandlerType">The concrete handler type that serves the request.</param>
/// <param name="ContextType">
///     The context type the request is created with: the <c>TContext</c> of its <c>RequestBase&lt;TContext&gt;</c>, or
///     <c>RequestContextBase</c> for a request that implements the request interfaces directly.
/// </param>
/// <param name="PreHandlers">The pre-handler attributes declared on the request.</param>
/// <param name="PostHandlers">The post-handler attributes declared on the request.</param>
/// <param name="PipelineExemptions">The pipeline exemptions declared on the request.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record RequestMetadata(
    Type HandlerType,
    Type ContextType,
    IPreHandlerAttribute[] PreHandlers,
    IPostHandlerAttribute[] PostHandlers,
    PipelineExemptionAttribute[] PipelineExemptions
);

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     An immutable snapshot describing how a single request type is wired in the current scope:
///     its handler, request context, the ordered pipeline behaviors and interceptors that apply,
///     and any issues found while resolving the binding. Returned by <see cref="ICqrsDiagnostics" />.
/// </summary>
/// <param name="RequestType">The request type this binding describes.</param>
/// <param name="ResponseType">
///     The result type the request produces; for streaming requests this is the
///     <c>IAsyncEnumerable&lt;TItem&gt;</c> the handler yields.
/// </param>
/// <param name="HandlerType">
///     The handler type registered to process the request, or <see langword="null" /> when no handler
///     is registered (a corresponding entry is then recorded in <paramref name="Issues" />).
/// </param>
/// <param name="ContextType">
///     The request context type used when executing the request, or <see langword="null" /> when it
///     could not be determined.
/// </param>
/// <param name="PipelineExemptions">
///     The pipeline behavior types the request opts out of, as declared by its exemption metadata.
/// </param>
/// <param name="PreHandlers">
///     The pre-handler interceptors that run before the handler, ordered by execution priority then type name.
/// </param>
/// <param name="PostHandlers">
///     The post-handler interceptors that run after the handler, ordered by execution priority then type name.
/// </param>
/// <param name="Pipeline">
///     The active pipeline behaviors wrapping the handler, in execution order; excludes behaviors the
///     request is exempted from.
/// </param>
/// <param name="ExemptedPipeline">
///     The registered pipeline behaviors skipped for this request because it is exempted from them,
///     in execution order; surfaced for introspection only.
/// </param>
/// <param name="Issues">
///     The diagnostic issues found while resolving the binding (missing handler, missing or faulted
///     context factory, pipeline resolution failures, and similar). Empty when the binding is fully resolved.
/// </param>
public sealed record CqrsRequestBinding(
    Type RequestType,
    Type ResponseType,
    Type? HandlerType,
    Type? ContextType,
    IReadOnlyList<Type> PipelineExemptions,
    IReadOnlyList<CqrsInterceptorBinding> PreHandlers,
    IReadOnlyList<CqrsInterceptorBinding> PostHandlers,
    IReadOnlyList<CqrsPipelineBehaviorBinding> Pipeline,
    IReadOnlyList<CqrsPipelineBehaviorBinding> ExemptedPipeline,
    IReadOnlyList<CqrsBindingIssue> Issues);
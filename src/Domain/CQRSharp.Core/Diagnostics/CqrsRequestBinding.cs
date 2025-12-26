namespace CQRSharp.Core.Diagnostics;

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

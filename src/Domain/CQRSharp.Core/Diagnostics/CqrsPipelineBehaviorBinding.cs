namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Describes a single pipeline behavior bound to a request, as surfaced by the diagnostics introspection API.
/// </summary>
/// <param name="BehaviorType">The concrete pipeline behavior type bound to the request.</param>
/// <param name="Priority">The execution priority of the behavior; lower values run earlier in the pipeline.</param>
/// <param name="IsExempted">
///     Whether the request exempts this behavior, causing it to be skipped during execution.
/// </param>
public sealed record CqrsPipelineBehaviorBinding(
    Type BehaviorType,
    int Priority,
    bool IsExempted);

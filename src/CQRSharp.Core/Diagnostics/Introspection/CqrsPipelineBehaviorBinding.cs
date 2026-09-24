namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Describes a single pipeline behavior bound to a request, as surfaced by the diagnostics introspection API. Whether
///     the behavior runs is which list of the <see cref="CqrsRequestBinding" /> it is in.
/// </summary>
/// <param name="BehaviorType">The concrete pipeline behavior type bound to the request.</param>
/// <param name="Priority">The execution priority of the behavior; lower values run earlier in the pipeline.</param>
public sealed record CqrsPipelineBehaviorBinding(
    Type BehaviorType,
    int Priority);

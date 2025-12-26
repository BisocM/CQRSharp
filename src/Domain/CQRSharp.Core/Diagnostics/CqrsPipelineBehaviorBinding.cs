namespace CQRSharp.Core.Diagnostics;

public sealed record CqrsPipelineBehaviorBinding(
    Type BehaviorType,
    int Priority,
    bool IsExempted);


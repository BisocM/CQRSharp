namespace CQRSharp.Core.Diagnostics;

public sealed record CqrsInterceptorBinding(
    Type AttributeType,
    int Priority);


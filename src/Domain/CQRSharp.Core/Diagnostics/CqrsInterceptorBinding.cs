namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Describes a single pre- or post-handler interceptor bound to a request, as surfaced by the diagnostics
///     introspection API.
/// </summary>
/// <param name="AttributeType">The interceptor attribute type bound to the request.</param>
/// <param name="Priority">The execution priority of the interceptor; lower values run earlier.</param>
public sealed record CqrsInterceptorBinding(
    Type AttributeType,
    int Priority);
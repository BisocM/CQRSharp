namespace CQRSharp.Sample.Application.Contexts;

/// <summary>
///     The context of the sample's requests: who is calling, which the rate limiter keys its buckets on. No creation time
///     is set here, so the dispatcher stamps the context from the application's <see cref="TimeProvider" />.
/// </summary>
public sealed class SampleRequestContext : RequestContextBase, IRateLimitedContext
{
    public required string UserId { get; init; }
}

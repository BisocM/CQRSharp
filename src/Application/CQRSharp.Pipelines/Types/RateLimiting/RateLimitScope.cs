namespace CQRSharp.Pipelines.Types.RateLimiting;

/// <summary>
///     Specifies the scope for rate limiting, determining how rate limits are applied.
/// </summary>
public enum RateLimitScope
{
    /// <summary>
    ///     Rate limiting applies globally for each user across all commands.
    /// </summary>
    Global,

    /// <summary>
    ///     Rate limiting applies per command for each user.
    /// </summary>
    PerCommand
}
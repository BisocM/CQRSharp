namespace CQRSharp.Core.Pipelines.Types.RateLimiting
{
    /// <summary>
    /// Class object for the options needed for the rate limited.
    /// </summary>
    public sealed class RateLimiterOptions
    {
        /// <summary>
        /// The maximum tokens that each user has before they hit the rate limit.
        /// </summary>
        public int MaxTokens { get; set; } = 3;
        
        /// <summary>
        /// How many tokens are to be replenished for each user per second.
        /// </summary>
        //TODO: This is atrocious and the customization is quite bad.
        public int ReplenishRatePerSecond { get; set; } = 1;
        
        /// <summary>
        /// The scope of the rate limiter.
        /// If the rate limiter is set to `Global`, that means that it would limit the user for *all* commands.
        /// If the rate limiter is set to `PerCommand`, that means that it would limit the user only for the specific command that triggered the rate limit.
        /// </summary>
        public RateLimitScope Scope { get; set; } = RateLimitScope.Global;
    }
}
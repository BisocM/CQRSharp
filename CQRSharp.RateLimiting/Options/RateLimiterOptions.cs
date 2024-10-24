using CQRSharp.RateLimiting.Enums;

namespace CQRSharp.RateLimiting.Options
{
    public sealed class RateLimiterOptions
    {
        public int MaxTokens { get; set; } = 3;
        public int ReplenishRatePerSecond { get; set; } = 1;
        public RateLimitScope Scope { get; set; } = RateLimitScope.Global;
    }
}
namespace CQRSharp.RateLimiting.Enums
{
    public enum RateLimitScope
    {
        /// <summary>
        /// Rate limiting applies globally for each user across all commands.
        /// </summary>
        Global,

        /// <summary>
        /// Rate limiting applies per command for each user.
        /// </summary>
        PerCommand
    }
}
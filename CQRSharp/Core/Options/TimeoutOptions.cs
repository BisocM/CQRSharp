namespace CQRSharp.Core.Options
{
    /// <summary>
    /// Configuration options for the timeout behavior.
    /// </summary>
    public sealed class TimeoutOptions
    {
        /// <summary>
        ///     The timeout for a command execution.
        /// </summary>
        /// <remarks>
        ///     The default value is <c>30 seconds</c>.
        /// </remarks>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}
using CQRSharp.Core.Pipeline.Attributes.Markers;
using CQRSharp.Core.Pipeline.Types;

namespace CQRSharp.Core.Options
{
    public sealed class DispatcherOptions
    {
        /// <summary>
        /// Determines whether properties marked with the <see cref="SensitiveDataAttribute"/> 
        /// should be logged in the command context.
        /// </summary>
        /// <remarks>
        /// The default value is <c>false</c>.
        /// </remarks>
        public bool EnableSensitiveDataLogging { get; set; } = false;

        /// <summary>
        /// Determines whether or not the execution context should be logged. Requires the implementation of the <see cref="ExecutionLoggingBehavior{TExecutable, TResult}"/> pipeline behavior.
        /// </summary>
        /// <remarks>
        /// The default value is <c>false</c>.
        /// </remarks>
        public bool EnableExecutionContextLogging { get; set; } = false;

        /// <summary>
        /// The maximum number of retries for a command execution.
        /// </summary>
        /// <remarks>
        /// The default value is <c>3</c>.
        /// </remarks>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// The timeout for a command execution.
        /// </summary>
        /// <remarks>
        /// The default value is <c>30 seconds</c>.
        /// </remarks>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        //TODO: Add fluent configuration properly.

        //public CQRSharpOptions EnableSensitiveLogging(bool enabled = false)
        //{
        //    EnableSensitiveDataLogging = enabled;
        //    return this;
        //}
    }
}

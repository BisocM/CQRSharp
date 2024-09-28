using CQRSharp.Core.Pipeline.Attributes.Markers;
using CQRSharp.Core.Pipeline.Types;

namespace CQRSharp.Core.Options
{
    public sealed class CQRSharpOptions
    {
        /// <summary>
        /// Determines whether or not properties marked with the <see cref="SensitiveDataAttribute"/> should be hidden from the command context logging.
        /// Defaults to false.
        /// </summary>
        public bool EnableSensitiveDataLogging { get; set; } = false;

        /// <summary>
        /// Determines whether or not the execution context should be logged. Requires the implementation of the <see cref="ExecutionLoggingBehavior{TExecutable, TResult}"/> pipeline behavior.
        /// </summary>
        public bool EnableExecutionContextLogging { get; set; } = false;
    }
}

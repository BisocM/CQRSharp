using CQRSharp.Shared.Data.Attributes.Pipelines;

namespace CQRSharp.Core.Options;

/// <summary>
///     Configuration options for the execution logging behavior.
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>
    ///     Determines whether properties marked with the <see cref="SensitiveDataAttribute" />
    ///     should be logged in the command context.
    /// </summary>
    /// <remarks>
    ///     The default value is <c>false</c>.
    /// </remarks>
    public bool EnableSensitiveDataLogging { get; set; } = false;

    /// <summary>
    ///     Determines whether the execution context should be logged. Requires the implementation of the execution
    ///     logging behaviour.
    /// </summary>
    /// <remarks>
    ///     The default value is <c>false</c>.
    /// </remarks>
    public bool EnableExecutionContextLogging { get; set; } = false;
}
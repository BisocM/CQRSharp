using System.Data;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Logging;

/// <summary>The sample's log messages, source-generated so a message is parsed once, not on every call.</summary>
internal static partial class SampleLog
{
    [LoggerMessage(9000, LogLevel.Information, "Handled {NotificationName} in {ElapsedMs:0.##}ms")]
    public static partial void NotificationHandled(ILogger logger, string notificationName, double elapsedMs);

    [LoggerMessage(9001, LogLevel.Information, "Intercepted {RequestName}: the handler {Outcome}")]
    public static partial void InterceptedRequest(ILogger logger, string requestName, string outcome);

    [LoggerMessage(9010, LogLevel.Debug, "Transaction begun ({IsolationLevel})")]
    public static partial void TransactionBegun(ILogger logger, IsolationLevel isolationLevel);

    [LoggerMessage(9011, LogLevel.Debug, "Transaction committed")]
    public static partial void TransactionCommitted(ILogger logger);

    [LoggerMessage(9012, LogLevel.Debug, "Transaction rolled back")]
    public static partial void TransactionRolledBack(ILogger logger);

    [LoggerMessage(9100, LogLevel.Information, "Self-test starting")]
    public static partial void SelfTestStarting(ILogger logger);

    [LoggerMessage(9101, LogLevel.Information, "Scenario {Scenario}")]
    public static partial void ScenarioStarting(ILogger logger, string scenario);

    [LoggerMessage(9102, LogLevel.Information, "Self-test passed: {Count} scenarios")]
    public static partial void SelfTestPassed(ILogger logger, int count);

    [LoggerMessage(9103, LogLevel.Critical, "Self-test failed in scenario {Scenario}")]
    public static partial void SelfTestFailed(ILogger logger, Exception exception, string scenario);
}

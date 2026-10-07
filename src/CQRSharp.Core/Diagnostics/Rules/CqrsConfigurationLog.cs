using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     What a <c>CQRCONF</c> rule logs where it is first met at runtime (the first dispatch of a request, the
///     first publish of a notification, the first use of the notification transports), once per service provider and type
///     or transport, under one category of their own.
/// </summary>
internal static partial class CqrsConfigurationLog
{
    /// <summary>The category the warnings are logged under.</summary>
    public const string Category = "CQRSharp.Core.Diagnostics.CqrsConfiguration";

    [LoggerMessage(1204, LogLevel.Warning, "CQRSharp configuration warning {Code} for {RequestName}: {Message}")]
    public static partial void RequestWarning(ILogger logger, string code, string requestName, string message);

    [LoggerMessage(1205, LogLevel.Warning, "CQRSharp configuration warning {Code} for {NotificationName}: {Message}")]
    public static partial void NotificationWarning(ILogger logger, string code, string notificationName, string message);

    [LoggerMessage(1206, LogLevel.Error, "CQRSharp configuration error {Code} for {NotificationName}: {Message}")]
    public static partial void NotificationError(ILogger logger, string code, string notificationName, string message);

    [LoggerMessage(1207, LogLevel.Warning, "CQRSharp configuration warning {Code} for transport {TransportName}: {Message}")]
    public static partial void TransportWarning(ILogger logger, string code, string transportName, string message);
}

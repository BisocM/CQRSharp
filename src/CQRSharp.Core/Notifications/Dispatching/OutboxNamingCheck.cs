using CQRSharp.Core.Diagnostics;
using CQRSharp.Persistence;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The first-use half of <c>CQRCONF003</c> / <c>CQRCONF010</c> for one handled, routed notification type in one
///     service provider: why the serializer gives it no name while an outbox mode is on, decided once, by the rule the
///     startup validator applies, at the first publish that would have stored it. A type that lost its
///     <c>[NotificationName]</c> to another module's type (<c>CQRCONF010</c>) is kept as a failure every such publish then
///     throws, since its declared durability cannot be honored; a type that simply has no name (<c>CQRCONF003</c>) is
///     logged once and delivered in-process, as before.
/// </summary>
internal sealed class OutboxNamingCheck(Type notificationType)
{
    private readonly object _gate = new();
    private volatile bool _checked;
    private string? _failure;

    /// <summary>
    ///     The type's configuration error, or <see langword="null" /> when a publish of it may proceed in-process; decided
    ///     first when this is the first publish the registered <paramref name="serializer" /> declined to name.
    /// </summary>
    public string? Failure(INotificationSerializer serializer, OutboxMode mode, ILogger logger)
    {
        if (!_checked) Decide(serializer, mode, logger);
        return _failure;
    }

    private void Decide(INotificationSerializer serializer, OutboxMode mode, ILogger logger)
    {
        lock (_gate)
        {
            if (_checked) return;

            switch (CqrsConfigurationRules.UnnamedHandledNotification(notificationType, serializer, mode))
            {
                case { Severity: CqrsBindingIssueSeverity.Error } error:
                    _failure = CqrsConfigurationRules.FailureMessage(error);
                    break;
                case { } warning:
                    CqrsConfigurationLog.NotificationWarning(logger, warning.Code, notificationType.Name, warning.Message);
                    break;
            }

            _checked = true;
        }
    }
}

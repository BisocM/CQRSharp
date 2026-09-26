using CQRSharp.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The first-use half of <c>CQRCONF011</c> / <c>CQRCONF012</c> for one durable, routed notification type in one
///     service provider: whether handlers registered by hand for it exist, which the outbox never reaches, decided once, by
///     the rule the startup validator applies, at its first publish that goes to the outbox, and logged. Delivery is left
///     as it is: the outbox delivers to subscriptions, an in-process publish to the hand-registered handlers too.
/// </summary>
/// <remarks>
///     Only built for a type the provider may have handlers registered by hand for; the generator never registers one, so
///     most durable types have no check at all. Telling the hand-registered handlers from the subscribed ones means
///     constructing them, which happens once, in the scope of that first publish.
/// </remarks>
/// <typeparam name="TNotification">The durable notification type.</typeparam>
internal sealed class DurableHandlersCheck<TNotification> where TNotification : INotification
{
    private readonly object _gate = new();
    private volatile bool _checked;

    /// <summary>Checks, the first time, the handlers registered by hand for the type, resolved from <paramref name="services" />.</summary>
    public void Check(NotificationPublisher publisher, IServiceProvider services, string name, ILogger logger)
    {
        if (_checked) return;

        lock (_gate)
        {
            if (_checked) return;

            try
            {
                if (publisher.HandRegisteredHandlerTypes<TNotification>(services) is { Length: > 0 } byHand)
                {
                    var issue = CqrsConfigurationRules.HandRegisteredHandlersOfDurableNotification(typeof(TNotification), name, byHand);
                    CqrsConfigurationLog.NotificationWarning(logger, issue.Code, typeof(TNotification).Name, issue.Message);
                }
            }
            catch (Exception ex)
            {
                // The durable publish itself constructs none of them: it is reported, not failed. An in-process publish of
                // the type fails with this same exception.
                var issue = CqrsConfigurationRules.UnresolvableHandRegisteredHandlers(typeof(TNotification), ex);
                CqrsConfigurationLog.NotificationError(logger, issue.Code, typeof(TNotification).Name, issue.Message);
            }

            _checked = true;
        }
    }
}

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Rejects a <see cref="NotificationOptions" /> whose strategy is not defined, at host start: a number bound from
///     configuration would otherwise fall through to running handlers concurrently on one scope, the mode the default
///     exists to avoid.
/// </summary>
internal sealed class NotificationOptionsValidator : OptionsValidator<NotificationOptions>
{
    protected override IEnumerable<string> Failures(NotificationOptions options)
    {
        if (!Enum.IsDefined(options.PublishStrategy))
            yield return "NotificationOptions.PublishStrategy must be a defined PublishStrategy.";
    }
}

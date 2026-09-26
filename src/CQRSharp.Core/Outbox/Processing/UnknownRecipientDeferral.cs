namespace CQRSharp.Core.Outbox;

/// <summary>
///     How long a message for a notification or handler this instance does not know is held back for another instance
///     that may know it: in a fleet running mixed versions, a newer instance may have stored it. The outbox processor
///     applies it to a claimed message, and the notification intake to a received one, so both give up at the same age.
/// </summary>
internal static class UnknownRecipientDeferral
{
    /// <summary>
    ///     Whether a message created at <paramref name="createdAt" /> is still within the grace period at
    ///     <paramref name="now" />, and if so until when to hold it. The delay grows with the message's age, between one
    ///     polling interval and the longest retry back-off: a message a newer instance is about to deliver waits one poll,
    ///     while one no instance knows is revisited a handful of times (its age about doubling each time), and never held
    ///     past the end of the grace period, so it is given up on time.
    /// </summary>
    public static bool TryDefer(OutboxProcessorOptions options, DateTime now, DateTime createdAt, out DateTime notBefore)
    {
        var age = now - createdAt;
        var grace = options.UnknownRecipientGracePeriod;
        if (age >= grace)
        {
            notBefore = default;
            return false;
        }

        var shortest = options.PollingInterval;
        var longest = options.Retry.MaxDelay > shortest ? options.Retry.MaxDelay : shortest;
        var delay = age < shortest ? shortest : age > longest ? longest : age;
        var remaining = grace - age;
        notBefore = now + (delay < remaining ? delay : remaining);
        return true;
    }
}

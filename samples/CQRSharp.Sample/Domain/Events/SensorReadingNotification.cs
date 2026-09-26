namespace CQRSharp.Sample.Domain.Events;

/// <summary>
///     A notification that is a value type. Under Native AOT the container cannot close the open-generic
///     <c>NotificationLoggingBehavior&lt;&gt;</c> over it, so the source-generated closed factory is what wraps its publish.
/// </summary>
public readonly struct SensorReadingNotification(double celsius) : INotification
{
    public double Celsius { get; } = celsius;
}

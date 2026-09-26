namespace CQRSharp.Sample.Domain.Events;

/// <summary>
///     Published when a user is created. The stable name makes it durable: published inside a transaction, it goes
///     through the outbox, and the generated serializer rebuilds it through its constructor.
/// </summary>
[NotificationName("sample.user.created")]
public sealed record UserCreatedNotification(Guid UserId, string Name) : INotification;

namespace CQRSharp;

/// <summary>
///     A common interface for all request contexts: the one thing every context carries is when it was created.
///     Concrete contexts add whatever a request needs (a user, a tenant, a correlation id).
/// </summary>
public interface IRequestContext
{
    /// <summary>
    ///     When the request was sent, in UTC. A <see cref="RequestContextBase" /> built without an explicit time is stamped
    ///     by the dispatcher from the application's <c>TimeProvider</c> when the request is sent.
    /// </summary>
    DateTime CreatedAt { get; }
}
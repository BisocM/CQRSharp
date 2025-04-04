namespace CQRSharp.Shared.Attributes;

/// <summary>
/// Enumeration for the various kinds of handler categories:
/// Command, Query, Notification, PipelineBehavior, etc.
/// </summary>
public enum HandlerKind
{
    Command,
    Query,
    Notification,
    PipelineBehavior
}

/// <summary>
/// Apply this attribute to an interface to declare
/// what kind of handler interface it is.
/// 
/// For example:
/// <code>
/// [HandlerType(HandlerKind.Command)]
/// public interface ICommandHandler&lt;TCommand&gt; { ... }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class HandlerTypeAttribute(HandlerKind kind) : Attribute
{
    public HandlerKind Kind { get; } = kind;
}

/// <summary>
/// Enumeration for the various kinds of requests:
/// Command, Query, etc.
/// </summary>
public enum RequestKind
{
    Command,
    Query,
    Notification,
    Unknown
}

/// <summary>
/// Apply this attribute to an interface that serves as a "request" marker,
/// e.g. IRequest, ICommand, IQuery&lt;TResult&gt;.
/// 
/// For example:
/// <code>
/// [RequestMarker(RequestKind.Command)]
/// public interface ICommand : IRequest {}
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class RequestMarkerAttribute(RequestKind kind) : Attribute
{
    public RequestKind Kind { get; } = kind;
}
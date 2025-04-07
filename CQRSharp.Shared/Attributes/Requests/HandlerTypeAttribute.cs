namespace CQRSharp.Shared.Attributes.Requests;

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
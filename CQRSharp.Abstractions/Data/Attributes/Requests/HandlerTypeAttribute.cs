namespace CQRSharp.Abstractions.Data.Attributes.Requests;

/// <summary>
///     Apply this attribute to an interface to declare
///     what kind of handler interface it is.
///     For example:
///     <code>
/// [HandlerType(HandlerKind.Command)]
/// public interface ICommandHandler&lt;TCommand&gt; { ... }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class HandlerTypeAttribute(HandlerKind kind) : Attribute
{
    /// <summary>
    ///     Gets the category of the handler interface as defined by the <see cref="HandlerKind" /> enumeration.
    ///     This property specifies whether the handler is a Command, Query, Notification, PipelineBehavior, or another kind.
    /// </summary>
    public HandlerKind Kind { get; } = kind;
}

/// <summary>
///     Enumeration for the various kinds of handler categories:
///     Command, Query, Notification, PipelineBehavior, etc.
/// </summary>
public enum HandlerKind
{
    Command,
    Query,
    Notification,
    PipelineBehavior
}
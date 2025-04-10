namespace CQRSharp.Shared.Data.Attributes.Requests;

/// <summary>
///     Apply this attribute to an interface that serves as a "request" marker,
///     e.g. IRequest, ICommand, IQuery&lt;TResult&gt;.
///     For example:
///     <code>
/// [RequestMarker(RequestKind.Command)]
/// public interface ICommand : IRequest {}
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class RequestMarkerAttribute(RequestKind kind) : Attribute
{
    public RequestKind Kind { get; } = kind;
}

/// <summary>
///     Enumeration for the various kinds of requests:
///     Command, Query, etc.
/// </summary>
public enum RequestKind
{
    Command,
    Query,
    Notification,
    Unknown
}
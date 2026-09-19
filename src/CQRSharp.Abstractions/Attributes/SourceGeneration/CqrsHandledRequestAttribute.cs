namespace CQRSharp.Abstractions.Attributes.SourceGeneration;

/// <summary>
///     Emitted by the CQRSharp source generator, one per request type that has a discovered handler, as an
///     assembly-level attribute. Tooling (the CQRSharp analyzers) reads these — across the current compilation and
///     referenced assemblies — to determine whether a request has a handler without scanning every type.
/// </summary>
/// <remarks>
///     This is generated metadata; you should not normally apply it by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CqrsHandledRequestAttribute(Type requestType) : Attribute
{
    /// <summary>The request type that has a handler.</summary>
    public Type RequestType { get; } = requestType;
}
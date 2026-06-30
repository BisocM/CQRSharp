namespace CQRSharp.Abstractions.Attributes.SourceGeneration;

/// <summary>
///     Emitted by the CQRSharp source generator, one per context type that has a discovered
///     <c>IRequestContextFactory&lt;TContext&gt;</c>, as an assembly-level attribute. The CQRSharp analyzers read these —
///     across the current compilation and referenced assemblies — to know which custom request contexts have a factory
///     without scanning every type, so they can warn when a custom-context request has none.
/// </summary>
/// <remarks>
///     This is generated metadata; you should not normally apply it by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CqrsRegisteredContextFactoryAttribute(Type contextType) : Attribute
{
    /// <summary>The context type that has a registered <c>IRequestContextFactory</c>.</summary>
    public Type ContextType { get; } = contextType;
}

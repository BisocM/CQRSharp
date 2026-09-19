namespace CQRSharp.Abstractions.Attributes.SourceGeneration;

/// <summary>
///     Identifies a CQRSharp framework "well-known" type so tooling (the source generator and analyzers) can recover
///     the real <see cref="System.Type" /> from referenced metadata instead of matching fully-qualified name strings.
/// </summary>
/// <remarks>
///     Applied at the assembly level — see the single declaration file in <c>CQRSharp.Core</c>, one entry per
///     <see cref="CqrsRole" />. Because the payload is a real <c>typeof(...)</c>, a rename or namespace move of an
///     anchor type is a compile error at that declaration rather than a silent generator/analyzer no-op. This is
///     compile-time metadata only; it is never constructed or read at runtime.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CqrsWellKnownTypeAttribute(CqrsRole role, Type type) : Attribute
{
    /// <summary>The well-known role this type fulfils.</summary>
    public CqrsRole Role { get; } = role;

    /// <summary>
    ///     The framework type fulfilling <see cref="Role" /> — an unbound generic definition (e.g.
    ///     <c>typeof(ICommandHandler&lt;,&gt;)</c>) for generic anchors, so arity is encoded structurally.
    /// </summary>
    public Type Type { get; } = type;
}
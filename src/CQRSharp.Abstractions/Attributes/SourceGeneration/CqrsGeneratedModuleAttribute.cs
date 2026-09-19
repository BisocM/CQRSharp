namespace CQRSharp.Abstractions.Attributes.SourceGeneration;

/// <summary>
///     Emitted by the CQRSharp source generator, once per assembly that contains at least one handler, as an
///     assembly-level attribute naming that assembly's generated module registrar. A composition-root assembly reads
///     these — across its referenced assemblies — to wire every module's registrations at compile time, which is what
///     lets a single <c>AddCqrsGenerated()</c> span multiple assemblies without the generated code colliding.
/// </summary>
/// <remarks>
///     This is generated metadata; you should not normally apply it by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class CqrsGeneratedModuleAttribute(Type registrarType) : Attribute
{
    /// <summary>The generated <c>CqrsModuleRegistrar</c> type whose <c>Register</c> method wires this assembly's module.</summary>
    public Type RegistrarType { get; } = registrarType;
}

namespace CQRSharp.Shared.Attributes.ExtensionMarkers;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GeneratorExtensionAttribute(GeneratorExtensionType registrationType) : Attribute
{
    /// <summary>
    /// Gets the type of registration represented by the <see cref="GeneratorExtensionAttribute"/>.
    /// </summary>
    public GeneratorExtensionType RegistrationType { get; } = registrationType;
}

public enum GeneratorExtensionType
{
    HandlerRegistration,
    PipelineRegistration,
}
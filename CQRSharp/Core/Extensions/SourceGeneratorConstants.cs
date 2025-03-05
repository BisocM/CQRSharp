namespace CQRSharp.Core.Extensions;

/// <summary>
/// Contains constant values used by the source generation process within the CQRSharp framework.
/// Particularly points towards the name of the assemblies and various classes.
/// </summary>
public static class SourceGeneratorConstants
{
    /// <summary>
    /// Represents the namespace that contains generated code within the CQRSharp framework.
    /// This namespace is utilized by the source generator to organize generated classes and types
    /// such as handler registries and other related constructs.
    /// </summary>
    public const string GeneratedNamespace = "CQRSharp.Generated";

    /// <summary>
    /// Refers to the name of the generated handler registry class within the CQRSharp framework.
    /// This class is dynamically created by the source generator and serves as a registry for handler
    /// registrations to streamline dependency injection and handler discovery.
    /// </summary>
    public const string HandlerRegistryClassName = "GeneratedHandlerRegistry";

    /// <summary>
    /// Represents the constant property name used to reference the generated mapping of handler registrations.
    /// This property serves as a key for accessing a static dictionary structure that maps handler metadata,
    /// including handler interfaces and their registration types, supporting dynamic handler execution and registry introspection.
    /// </summary>
    public const string RegistrationMapPropertyName = "RegistrationMap";

    /// <summary>
    /// Represents the constant property name used to reference the generated metadata mapping
    /// within the CQRSharp framework. This property acts as a key for accessing a static structure
    /// that maps metadata information, enabling efficient storage and retrieval of metadata
    /// relevant to dynamically generated components and their interactions.
    /// </summary>
    public const string MetadataMapPropertyName = "MetadataMap";

    /// <summary>
    /// Refers to the name of the generated pipeline registry class within the CQRSharp framework.
    /// This class is dynamically created by the source generator to register and manage middleware
    /// or processing pipelines, facilitating streamlined execution of request and response flows.
    /// </summary>
    public const string PipelineRegistryClassName = "GeneratedPipelineRegistry";
    
    public const string PipelineMapPropertyName = "PipelineMap";
}
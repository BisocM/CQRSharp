using System;
using CQRSharp.Generators;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // ============================================================================================================
    // Equatable, symbol-free models. The syntax transform projects each candidate type into a CandidateModel and a
    // small compilation-level KnownSnapshot; everything downstream generates from these records. Because the records
    // are value-equatable (EquatableArray for collections), an edit that doesn't change a type's CQRSharp-relevant
    // shape produces identical records and the pipeline short-circuits — and no Compilation/symbols are held across
    // generations (the former CompilationProvider.Combine leaked both).
    // ============================================================================================================

    private enum HandlerKind
    {
        Command,
        Query,
        Stream
    }

    private enum RequestKind
    {
        Command,
        Query,
        Stream
    }

    [Flags]
    private enum ExceptionHookKind
    {
        None = 0,
        Action = 1,
        Handler = 2
    }

    /// <summary>Compilation-level facts resolved once (not per candidate) and shared by the generators.</summary>
    private sealed record KnownSnapshot(
        bool AbstractionsPresent,
        bool CoreReferenced,
        EquatableArray<string> MissingRequiredRoleNames,
        bool HasPipelinesBuilder,
        string CommandResultTypeName,
        string RequestContextBaseTypeName,
        string PreHandlerInterfaceName,
        string PostHandlerInterfaceName,
        string PipelineExemptionAttributeName,
        string ModuleNamespace,
        EquatableArray<string> ReferencedModuleRegistrars);

    /// <summary>A single CQRSharp-handler interface a concrete, accessible type implements.</summary>
    private sealed record HandlerImplModel(
        string ImplTypeName,
        HandlerKind Kind,
        string RequestTypeName,
        string? ResultTypeName,
        string InterfaceNameNullable,
        string InterfaceNameOrdinal,
        RequestMetadataModel RequestMetadata);

    /// <summary>A pre-/post-handler or pipeline-exemption attribute, rendered to its construction expression.</summary>
    private sealed record AttributeModel(
        string AttributeTypeName,
        EquatableArray<string> ConstructorArgs);

    /// <summary>
    ///     The request-derived metadata the request registry emits, read from the request type via a handler binding
    ///     (so it works whether the request type is local or in a referenced assembly).
    /// </summary>
    private sealed record RequestMetadataModel(
        string ContextTypeName,
        EquatableArray<AttributeModel> PreHandlers,
        EquatableArray<AttributeModel> PostHandlers,
        EquatableArray<AttributeModel> PipelineExemptions);

    /// <summary>A locally-declared request type (ICommand / IQuery&lt;T&gt; / IStreamRequest&lt;T&gt;) for dispatch and diagnostics.</summary>
    private sealed record RequestModel(
        RequestKind Kind,
        string RequestTypeName,
        string RequestTypeNameNullable,
        string? ResultOrItemNullable,
        string? QueryResultNonNullable);

    /// <summary>An exception action/handler hook for a request, with the request's resolved result type.</summary>
    private sealed record ExceptionHookModel(
        string RequestTypeName,
        string ExceptionTypeName,
        string ResultTypeName,
        int ExceptionInheritanceDepth,
        ExceptionHookKind Kind);

    /// <summary>A notification type: its stable name (if any) and the serializable object graph (or the CQRGEN005 reason).</summary>
    private sealed record NotificationModel(
        string TypeName,
        string? StableName,
        OutboxObjectModel? OutboxRoot,
        string? OutboxError,
        LocationInfo? OutboxErrorLocation);

    private sealed record CandidateModel(
        string TypeName,
        bool IsAccessible,
        bool IsConcrete,
        LocationInfo? Location,
        bool ImplementsAnyKnownHandlerInterface,
        EquatableArray<HandlerImplModel> Handlers,
        EquatableArray<string> HandlerForwarderInterfaces,
        RequestModel? Request,
        NotificationModel? Notification,
        EquatableArray<string> HandledNotifications,
        EquatableArray<string> ContextFactories,
        EquatableArray<ExceptionHookModel> ExceptionHooks,
        string? AotOpenGenericBehaviorName,
        bool IsOpenGenericHandler);

    // ---- Outbox serializer graph (was reference-equality classes; now value-equatable records) ----

    private enum OutboxScalarKind
    {
        String,
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        Guid,
        DateTime,
        DateTimeOffset,
        TimeSpan
    }

    private enum OutboxValueKind
    {
        Scalar,
        Enum,
        Object,
        Collection
    }

    private sealed record OutboxValueModel(
        OutboxValueKind Kind,
        string LocalTypeName,
        string NonNullableTypeName)
    {
        public bool IsNullableValueType { get; init; }
        public bool IsNullableReferenceType { get; init; }
        public bool IsReferenceType { get; init; }
        public OutboxScalarKind ScalarKind { get; init; }
        public string? EnumUnderlyingTypeName { get; init; }
        public OutboxObjectModel? ObjectModel { get; init; }
        public OutboxValueModel? Element { get; init; }
        public bool CollectionIsArray { get; init; }

        public static OutboxValueModel Scalar(
            OutboxScalarKind kind, string localTypeName, string nonNullableTypeName,
            bool isNullableValueType, bool isNullableReferenceType) =>
            new(OutboxValueKind.Scalar, localTypeName, nonNullableTypeName)
            {
                ScalarKind = kind,
                IsNullableValueType = isNullableValueType,
                IsNullableReferenceType = isNullableReferenceType
            };

        public static OutboxValueModel Enum(
            string localTypeName, string nonNullableTypeName, string enumUnderlyingTypeName, bool isNullableValueType) =>
            new(OutboxValueKind.Enum, localTypeName, nonNullableTypeName)
            {
                EnumUnderlyingTypeName = enumUnderlyingTypeName,
                IsNullableValueType = isNullableValueType
            };

        public static OutboxValueModel Object(
            string localTypeName, OutboxObjectModel objectModel, bool isNullableValueType, bool isReferenceType) =>
            new(OutboxValueKind.Object, localTypeName, objectModel.TypeName)
            {
                ObjectModel = objectModel,
                IsNullableValueType = isNullableValueType,
                IsReferenceType = isReferenceType
            };

        public static OutboxValueModel Collection(string localTypeName, OutboxValueModel element, bool isArray) =>
            new(OutboxValueKind.Collection, localTypeName, localTypeName)
            {
                Element = element,
                CollectionIsArray = isArray,
                IsReferenceType = true
            };
    }

    private sealed record OutboxMemberModel(
        string PropertyName,
        string JsonName,
        string LocalName,
        OutboxValueModel Value,
        bool CanInitialize)
    {
        public bool IsConstructorParameter { get; init; }
    }

    private sealed record OutboxObjectModel(
        string TypeName,
        EquatableArray<OutboxMemberModel> Members,
        string ConstructorExpression,
        string HelperId);
}

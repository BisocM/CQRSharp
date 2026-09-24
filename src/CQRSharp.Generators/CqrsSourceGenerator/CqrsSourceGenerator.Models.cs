using System;
using CQRSharp.Generators;
using CQRSharp.Shared;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // ============================================================================================================
    // Equatable, symbol-free models. The syntax transform projects each candidate type into a CandidateModel and the
    // compilation into a KnownSnapshot; both outputs work from these records alone. Collections are EquatableArrays, so
    // equal content compares equal. Source locations exist only for diagnostics: the generation output receives the
    // models with them stripped, so an edit that only moves code does not re-run code generation.
    // ============================================================================================================

    [Flags]
    private enum ExceptionHookKind
    {
        None = 0,
        Action = 1,
        Handler = 2
    }

    /// <summary>Compilation-level facts, recomputed per compilation; the steps after it re-run only when they change.</summary>
    private sealed record KnownSnapshot(
        bool CoreReferenced,
        EquatableArray<string> MissingRequiredTypeNames,
        bool HasPipelinesBuilder,
        string ModuleNamespace,
        EquatableArray<string> ReferencedModuleRegistrars,
        int VisibleForeignBootstraps,
        bool ForeignBootstrapCoversGraph,
        EquatableArray<ClosedBehaviorModel> ReferencedClosedBehaviors,
        EquatableArray<ClosedBehaviorGapModel> ReferencedClosedBehaviorGaps,
        EquatableArray<ReferencedContextFactoryModel> AmbiguousReferencedContextFactories);

    /// <summary>
    ///     A context factory a referenced assembly's module registers for a context type that another referenced module
    ///     registers one for too (CQRGEN019, unless this assembly declares its own). <c>RegistrarName</c> orders them the
    ///     way the bootstrap registers their modules: the last one's factory is the one used. <c>ContextTypeName</c> is
    ///     fully qualified, as this assembly's own factories name their context types.
    /// </summary>
    private sealed record ReferencedContextFactoryModel(
        string ContextTypeName,
        string ContextDisplayName,
        string FactoryTypeName,
        string AssemblyName,
        string RegistrarName);

    /// <summary>
    ///     A call of the generated <c>AddCqrsGenerated</c>: where this assembly composes its reference graph. Plain when
    ///     it is an extension-method call on a service collection rather than a call through the bootstrap type.
    /// </summary>
    private sealed record BootstrapCallSiteModel(LocationInfo Location, bool IsPlain);

    /// <summary>
    ///     A request as this module dispatches it, read from the request type's own shape (see
    ///     <see cref="CqrsKnownSymbols.ShapeOf" />): declared in this compilation, or handled here without being declared
    ///     here.
    /// </summary>
    /// <param name="Kind">How the request is dispatched.</param>
    /// <param name="RequestTypeName">The request type, fully qualified.</param>
    /// <param name="RequestTypeNameNullable">The request type as a generic argument (nullable annotations kept).</param>
    /// <param name="ResponseTypeName">The <c>TResponse</c> it is dispatched with (see <see cref="CqrsRequestShape.Response" />).</param>
    /// <param name="ResultOrItemNullable">The query result, <c>CommandResult&lt;T&gt;</c> or streamed item as a generic argument; null for a command.</param>
    /// <param name="ClosedBehaviors">The open-generic behaviors closed over it, when its result or item is a value type.</param>
    /// <param name="ClosedBehaviorGaps">The behaviors that apply to it but that generated code cannot close.</param>
    private sealed record RequestModel(
        CqrsRequestKind Kind,
        string RequestTypeName,
        string RequestTypeNameNullable,
        string ResponseTypeName,
        string? ResultOrItemNullable,
        EquatableArray<ClosedBehaviorModel> ClosedBehaviors,
        EquatableArray<ClosedBehaviorGapModel> ClosedBehaviorGaps);

    /// <summary>A dispatch-handler interface a concrete, accessible type implements, bound to its request.</summary>
    private sealed record HandlerImplModel(
        string ImplTypeName,
        string InterfaceNameNullable,
        string InterfaceNameOrdinal,
        RequestModel Request,
        RequestMetadataModel RequestMetadata);

    /// <summary>A pre-/post-handler or pipeline-exemption attribute, rendered to its construction expression.</summary>
    private sealed record AttributeModel(
        string AttributeTypeName,
        EquatableArray<string> ConstructorArgs,
        EquatableArray<string> NamedArgs);

    /// <summary>
    ///     The request-derived metadata the request registry emits, read from the request type (and, as reflection's
    ///     <c>GetCustomAttributes(inherit: true)</c> does, its base classes) via a handler binding, so it works whether
    ///     the request type is local or in a referenced assembly. <c>SkippedAttributes</c> describes the attributes
    ///     generated code cannot reproduce (CQRGEN016).
    /// </summary>
    private sealed record RequestMetadataModel(
        string ContextTypeName,
        EquatableArray<AttributeModel> PreHandlers,
        EquatableArray<AttributeModel> PostHandlers,
        EquatableArray<AttributeModel> PipelineExemptions,
        EquatableArray<string> SkippedAttributes);

    /// <summary>
    ///     An open-generic behavior closed over a request whose result (or streamed item) is a value type, or over a
    ///     value-type notification: the container cannot close it under Native AOT, so the module registers a generated
    ///     factory for it instead. <c>SuppressedWarnings</c> are the diagnostics naming the behavior raises ([Obsolete],
    ///     [Experimental]); <c>RelaxesNotNull</c> is set when it closes a <c>notnull</c> type parameter over a nullable
    ///     value type, which the container accepts and the compiler reports (CS8714).
    /// </summary>
    private sealed record ClosedBehaviorModel(
        string OpenTypeName,
        string ServiceTypeName,
        string ClosedTypeName,
        EquatableArray<string> SuppressedWarnings,
        bool RelaxesNotNull);

    /// <summary>
    ///     An open-generic behavior that applies to a value-type-result request or a value-type notification (the target)
    ///     but that generated code cannot close, named the way the runtime sees it, so the runtime can refuse to skip it
    ///     silently. The target is named by its closed behavior interface when generated code can name it, otherwise by
    ///     its runtime name and the open behavior interface (<c>ServiceDefinitionName</c>).
    /// </summary>
    private sealed record ClosedBehaviorGapModel(
        string? ServiceTypeName,
        string TargetRuntimeName,
        string TargetAssemblyName,
        string ServiceDefinitionName,
        string BehaviorRuntimeName,
        string BehaviorAssemblyName,
        string Reason);

    /// <summary>An exception action/handler hook for a request, with the response the request is dispatched with.</summary>
    private sealed record ExceptionHookModel(
        string RequestTypeName,
        string ExceptionTypeName,
        string ResponseTypeName,
        ExceptionHookKind Kind);

    /// <summary>
    ///     A handler interface whose result type is not the one its request is dispatched with (a covariant widening the
    ///     compiler accepts but the dispatcher can never call): CQRGEN017.
    /// </summary>
    private sealed record ResultMismatchModel(
        string InterfaceName,
        string RequestTypeName,
        string DeclaredTypeName,
        string ExpectedTypeName);

    /// <summary>
    ///     A notification type: its stable name (if any), the serializable object graph (or the CQRGEN005 reason), and
    ///     the property its <c>[NotificationName(PartitionBy = ...)]</c> names (unresolved when no readable property of
    ///     that name exists, which is CQRGEN011).
    /// </summary>
    private sealed record NotificationModel(
        string TypeName,
        string? StableName,
        OutboxObjectModel? OutboxRoot,
        string? OutboxError,
        string? PartitionBy,
        bool PartitionByResolved,
        LocationInfo? Location);

    /// <summary>
    ///     An idempotent request's automatic payload fingerprint: the write-only object graph the generated fingerprinter
    ///     hashes, or the reason it cannot be built (CQRGEN014).
    /// </summary>
    private sealed record FingerprintModel(
        string TypeName,
        OutboxObjectModel? Root,
        string? Error);

    /// <summary>
    ///     One type the transform found CQRSharp-relevant, with everything generated code emits for it.
    ///     <c>SuppressedWarnings</c> are the diagnostic ids naming it and the types it binds raises ([Obsolete] with a
    ///     DiagnosticId, [Experimental]), which the generated files that name them suppress.
    /// </summary>
    private sealed record CandidateModel(
        string TypeName,
        bool IsAccessible,
        bool IsConcrete,
        LocationInfo? Location,
        bool ImplementsAnyKnownHandlerInterface,
        EquatableArray<HandlerImplModel> Handlers,
        EquatableArray<string> HandlerForwarderInterfaces,
        EquatableArray<string> DiscoveredServiceInterfaces,
        RequestModel? Request,
        FingerprintModel? Fingerprint,
        EquatableArray<FingerprintModel> HandledRequestFingerprints,
        NotificationModel? Notification,
        EquatableArray<string> HandledNotifications,
        EquatableArray<string> HandledConcreteNotifications,
        string? NotificationHandlerName,
        bool NotificationHandlerNameIsBlank,
        EquatableArray<string> ContextFactories,
        bool RegistersContextFactories,
        EquatableArray<ExceptionHookModel> ExceptionHooks,
        EquatableArray<ClosedBehaviorModel> NotificationClosedBehaviors,
        EquatableArray<ClosedBehaviorGapModel> NotificationClosedBehaviorGaps,
        bool IsOpenGenericHandler,
        EquatableArray<string> InaccessibleBoundTypeNames,
        EquatableArray<ResultMismatchModel> ResultMismatches,
        EquatableArray<string> SuppressedWarnings)
    {
        /// <summary>The model the generation output works from: the same, without the locations only diagnostics use.</summary>
        public CandidateModel WithoutLocations()
            => Location is null && Notification?.Location is null
                ? this
                : this with { Location = null, Notification = Notification is null ? null : Notification with { Location = null } };
    }

    // ---- Outbox serializer graph ----

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
        Char,
        Guid,
        DateTime,
        DateTimeOffset,
        TimeSpan,
        DateOnly,
        TimeOnly,
        Uri
    }

    private enum OutboxValueKind
    {
        Scalar,
        Enum,
        Object,
        Collection,
        Dictionary
    }

    /// <summary>The concrete type a collection is read back into: an array, a <c>List&lt;T&gt;</c> or a <c>HashSet&lt;T&gt;</c>.</summary>
    private enum OutboxCollectionShape
    {
        Array,
        List,
        Set
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

        /// <summary>A collection's element, or a dictionary's value.</summary>
        public OutboxValueModel? Element { get; init; }

        public OutboxCollectionShape CollectionShape { get; init; }

        public static OutboxValueModel Scalar(
            OutboxScalarKind kind, string localTypeName, string nonNullableTypeName,
            bool isNullableValueType, bool isNullableReferenceType, bool isReferenceType) =>
            new(OutboxValueKind.Scalar, localTypeName, nonNullableTypeName)
            {
                ScalarKind = kind,
                IsNullableValueType = isNullableValueType,
                IsNullableReferenceType = isNullableReferenceType,
                IsReferenceType = isReferenceType
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

        public static OutboxValueModel Collection(string localTypeName, OutboxValueModel element, OutboxCollectionShape shape) =>
            new(OutboxValueKind.Collection, localTypeName, localTypeName)
            {
                Element = element,
                CollectionShape = shape,
                IsReferenceType = true
            };

        public static OutboxValueModel Dictionary(string localTypeName, OutboxValueModel value) =>
            new(OutboxValueKind.Dictionary, localTypeName, localTypeName)
            {
                Element = value,
                IsReferenceType = true
            };
    }

    /// <summary>
    ///     One serialized property. <c>DefaultValueExpression</c> is the explicit default of the constructor parameter it is
    ///     passed to, used when a stored payload lacks it.
    /// </summary>
    private sealed record OutboxMemberModel(
        string PropertyName,
        string JsonName,
        string LocalName,
        OutboxValueModel Value,
        bool CanInitialize,
        bool IsRequired)
    {
        public bool IsConstructorParameter { get; init; }
        public string? DefaultValueExpression { get; init; }
    }

    private sealed record OutboxObjectModel(
        string TypeName,
        EquatableArray<OutboxMemberModel> Members,
        string ConstructorExpression,
        string HelperId);
}

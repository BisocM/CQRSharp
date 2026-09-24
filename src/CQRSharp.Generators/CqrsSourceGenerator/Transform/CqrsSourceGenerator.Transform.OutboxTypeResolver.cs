using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    /// <summary>
    ///     Builds the recursive outbox object/value graph for a notification type (and the write-only graph of an
    ///     idempotent request's fingerprint). Runs in the transform (it needs symbols); the emitter consumes the resulting
    ///     equatable records. Every shape it accepts is written the way System.Text.Json writes it, so a payload either
    ///     one wrote reads back with the other. STJ source generation cannot be used here — see
    ///     <c>CqrsSourceGenerator.OutboxSerialization.cs</c>.
    /// </summary>
    private sealed class OutboxTypeResolver
    {
        private const string SupportedShapes =
            "a string, bool, char, integer or floating-point number, decimal, Guid, DateTime, DateTimeOffset, TimeSpan, DateOnly, TimeOnly or Uri; " +
            "an enum; a single-dimension array, List/IList/IReadOnlyList/ICollection/IReadOnlyCollection/IEnumerable or HashSet/ISet/IReadOnlySet of a supported element; " +
            "a Dictionary/IDictionary/IReadOnlyDictionary with string keys; or a class, struct or record of its own made of these";

        // System.Text.Json.Serialization.JsonIgnoreCondition values that still round-trip a member: Never, WhenWritingDefault
        // and WhenWritingNull only leave a value out of a payload when it is the default anyway, which the reader restores.
        // Always, and WhenWriting/WhenReading (which drop one direction), exclude it; so does any value not known here.
        private static readonly int[] RoundTrippingIgnoreConditions = { 0, 2, 3 };

        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol? _guid;
        private readonly INamedTypeSymbol? _dateTime;
        private readonly INamedTypeSymbol? _dateTimeOffset;
        private readonly INamedTypeSymbol? _timeSpan;
        private readonly INamedTypeSymbol? _dateOnly;
        private readonly INamedTypeSymbol? _timeOnly;
        private readonly INamedTypeSymbol? _uri;
        private readonly INamedTypeSymbol? _nullable;
        private readonly INamedTypeSymbol? _jsonPropertyName;
        private readonly INamedTypeSymbol? _jsonIgnore;
        private readonly INamedTypeSymbol? _jsonConstructor;
        private readonly IPropertySymbol? _partitionKeyMember;
        private readonly INamedTypeSymbol? _ienumerableOfT;
        private readonly INamedTypeSymbol?[] _listLikeDefs;
        private readonly INamedTypeSymbol?[] _setDefs;
        private readonly INamedTypeSymbol?[] _dictionaryDefs;

        private readonly Dictionary<ITypeSymbol, OutboxObjectModel> _objectCache =
            new(SymbolEqualityComparer.Default);

        // Write-only models (request fingerprints) never deserialize, so they need no constructor and may include
        // get-only computed properties; the exclusion hook drops properties that are runtime state, not payload.
        private readonly bool _writeOnly;
        private readonly Func<IPropertySymbol, bool>? _excludeProperty;

        private bool IsPartitionKeyImplementation(INamedTypeSymbol type, IPropertySymbol property)
            => _partitionKeyMember is not null &&
               type.FindImplementationForInterfaceMember(_partitionKeyMember) is IPropertySymbol implementation &&
               SymbolEqualityComparer.Default.Equals(implementation, property);

        public OutboxTypeResolver(Compilation compilation, bool writeOnly = false, Func<IPropertySymbol, bool>? excludeProperty = null)
        {
            _compilation = compilation;
            _writeOnly = writeOnly;
            _excludeProperty = excludeProperty;
            _guid = compilation.GetTypeByMetadataName("System.Guid");
            _dateTime = compilation.GetTypeByMetadataName("System.DateTime");
            _dateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
            _timeSpan = compilation.GetTypeByMetadataName("System.TimeSpan");
            _dateOnly = compilation.GetTypeByMetadataName("System.DateOnly");
            _timeOnly = compilation.GetTypeByMetadataName("System.TimeOnly");
            _uri = compilation.GetTypeByMetadataName("System.Uri");
            _nullable = compilation.GetTypeByMetadataName("System.Nullable`1");
            _jsonPropertyName = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
            _jsonIgnore = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
            _jsonConstructor = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConstructorAttribute");
            _partitionKeyMember = CqrsKnownSymbols.For(compilation).IPartitionedNotification?
                .GetMembers("PartitionKey").OfType<IPropertySymbol>().FirstOrDefault();
            _ienumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
            _listLikeDefs = new[]
            {
                compilation.GetTypeByMetadataName("System.Collections.Generic.List`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.ICollection`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyCollection`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")
            };
            _setDefs = new[]
            {
                compilation.GetTypeByMetadataName("System.Collections.Generic.HashSet`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.ISet`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlySet`1")
            };
            _dictionaryDefs = new[]
            {
                compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2")
            };
        }

        public bool TryBuildObjectModel(
            INamedTypeSymbol type,
            HashSet<ITypeSymbol> inProgress,
            out OutboxObjectModel? model,
            StringBuilder reason)
        {
            if (_objectCache.TryGetValue(type, out var cached))
            {
                model = cached;
                return true;
            }

            model = null;

            if (inProgress.Contains(type))
            {
                reason.Append($"circular reference through '{type.ToDisplayString(Fq)}' is not supported. ");
                return false;
            }

            inProgress.Add(type);
            try
            {
                // The payload is the public and internal instance properties; a member internal to another assembly is
                // still one of them, and is reported below rather than silently left out.
                var properties = GetDeclaredAndInheritedProperties(type)
                    .Where(p =>
                        !p.IsStatic &&
                        !p.IsIndexer &&
                        p.GetMethod is { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal } &&
                        p.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
                    .Where(p => !IsIgnored(p))
                    // IPartitionedNotification.PartitionKey is derived from the rest of the payload (that is the point of
                    // the interface), so it is neither written nor restored: a computed, get-only key must not make the
                    // notification unserializable.
                    .Where(p => !IsPartitionKeyImplementation(type, p))
                    .Where(p => _excludeProperty is null || !_excludeProperty(p))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();

                // A member internal to another assembly is only seen here when that assembly's internals are imported
                // (InternalsVisibleTo grants this one access, or the compilation imports every member). Without access
                // the generated code could not name it, and leaving it out would change the payload without a word.
                var unreadable = properties.FirstOrDefault(p => !GeneratedCodeAccessibility.IsAccessible(p.GetMethod!, _compilation));
                if (unreadable is not null)
                {
                    reason.Append($"member '{unreadable.Name}' of '{type.ToDisplayString(Fq)}' is internal to another assembly, so it cannot be read here. ");
                    return false;
                }

                // A member the payload leaves out (by [JsonIgnore], as the derived partition key, or as request
                // infrastructure) cannot be omitted from an object initializer when it is required - unless a
                // constructor declares that it sets the required members itself.
                if (!_writeOnly && !HasSetsRequiredMembersConstructor(type))
                {
                    var excludedRequired = GetDeclaredAndInheritedProperties(type)
                        .FirstOrDefault(p => p.IsRequired && !properties.Contains(p, SymbolEqualityComparer.Default));
                    if (excludedRequired is not null)
                    {
                        reason.Append($"required member '{excludedRequired.Name}' is excluded from the payload, so the type cannot be constructed when it is read back; make it optional or include it. ");
                        return false;
                    }

                    // Required fields are never written by the generated reader (it initializes properties only).
                    var requiredField = RequiredFields(type).FirstOrDefault();
                    if (requiredField is not null)
                    {
                        reason.Append($"required field '{requiredField.Name}' cannot be set when the type is read back; make it a property or not required. ");
                        return false;
                    }
                }

                var members = new OutboxMemberModel[properties.Length];
                var labels = new HashSet<string>(StringComparer.Ordinal);

                for (var i = 0; i < properties.Length; i++)
                {
                    var property = properties[i];
                    var propTypeName = property.Type.ToDisplayString(Fq);

                    if (!TryBuildValueModel(property.Type, inProgress, out var value, reason) || value is null)
                    {
                        reason.Append($"Unsupported property '{property.Name}' of type '{propTypeName}'. ");
                        return false;
                    }

                    var canInitialize = property.SetMethod is not null && GeneratedCodeAccessibility.IsAccessible(property.SetMethod, _compilation);

                    var jsonName = GetJsonPropertyName(property, _jsonPropertyName);

                    // Every member is read under its JSON name and its property name; two members sharing a label
                    // would be one ambiguous case in the generated reader.
                    if (!labels.Add(jsonName))
                    {
                        reason.Append($"property '{property.Name}' is read under the JSON name '{jsonName}', which another property is already read under, so the payload is ambiguous. ");
                        return false;
                    }

                    if (!string.Equals(jsonName, property.Name, StringComparison.Ordinal) && !labels.Add(property.Name))
                    {
                        reason.Append($"property '{property.Name}' is also read under its own name, which another property is already read under (its JSON name), so the payload is ambiguous. ");
                        return false;
                    }

                    // Locals are numbered, never named after a property: no property name can then collide with one.
                    members[i] = new OutboxMemberModel(
                        property.Name,
                        jsonName,
                        "__m" + i.ToString(CultureInfo.InvariantCulture),
                        value,
                        canInitialize,
                        property.IsRequired);
                }

                var constructorExpression = "default!";
                if (!_writeOnly && !TrySelectConstructor(type, members, out constructorExpression, reason))
                    return false;

                var helperId = CreateIdentifierSuffix(type.ToDisplayString(Fq), "T_");
                var built = new OutboxObjectModel(
                    type.ToDisplayString(Fq),
                    new EquatableArray<OutboxMemberModel>(members),
                    constructorExpression,
                    helperId);

                _objectCache[type] = built;
                model = built;
                return true;
            }
            finally
            {
                inProgress.Remove(type);
            }
        }

        // [JsonIgnore] leaves a member out of the payload only when it never round-trips: unconditionally (the default
        // Condition is Always), or in one direction.
        private bool IsIgnored(IPropertySymbol property)
        {
            if (_jsonIgnore is null) return false;

            foreach (var attribute in property.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _jsonIgnore)) continue;

                var condition = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Condition").Value;
                return condition.Kind != TypedConstantKind.Enum || condition.Value is not int value ||
                       Array.IndexOf(RoundTrippingIgnoreConditions, value) < 0;
            }

            return false;
        }

        public bool TryBuildValueModel(
            ITypeSymbol type,
            HashSet<ITypeSymbol> inProgress,
            out OutboxValueModel? model,
            StringBuilder reason)
        {
            model = null;

            var localTypeName = type.ToDisplayString(Fq);
            var isNullableValueType = false;
            var underlying = type;

            if (_nullable is not null &&
                type is INamedTypeSymbol named &&
                named.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _nullable) &&
                named.TypeArguments.Length == 1)
            {
                isNullableValueType = true;
                underlying = named.TypeArguments[0];
            }

            var nonNullableTypeName = underlying.ToDisplayString(Fq);
            // Only a reference declared non-nullable in a nullable-enabled context is read back as non-null: in an
            // oblivious (#nullable disable) context a null is as legitimate a value as any other.
            var isNullableReferenceType = !isNullableValueType && type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.NotAnnotated;

            if (TryGetScalarKind(underlying, out var scalarKind))
            {
                model = OutboxValueModel.Scalar(scalarKind, localTypeName, nonNullableTypeName, isNullableValueType, isNullableReferenceType, underlying.IsReferenceType);
                return true;
            }

            if (underlying.TypeKind == TypeKind.Enum && underlying is INamedTypeSymbol enumType)
            {
                var enumUnderlying = enumType.EnumUnderlyingType?.ToDisplayString(Fq);
                if (enumUnderlying is null)
                {
                    reason.Append($"enum '{nonNullableTypeName}' has no resolvable underlying type. ");
                    return false;
                }

                model = OutboxValueModel.Enum(localTypeName, nonNullableTypeName, enumUnderlying, isNullableValueType);
                return true;
            }

            if (type is IArrayTypeSymbol array)
            {
                if (array.Rank != 1)
                {
                    reason.Append($"multi-dimensional array '{localTypeName}' is not supported. ");
                    return false;
                }

                if (!TryBuildValueModel(array.ElementType, inProgress, out var elem, reason) || elem is null)
                    return false;

                model = OutboxValueModel.Collection(localTypeName, elem, OutboxCollectionShape.Array);
                return true;
            }

            if (underlying is INamedTypeSymbol { IsGenericType: true } generic)
            {
                var definition = generic.OriginalDefinition;
                var shape = IsOneOf(definition, _listLikeDefs) ? OutboxCollectionShape.List
                    : IsOneOf(definition, _setDefs) ? OutboxCollectionShape.Set
                    : (OutboxCollectionShape?)null;
                if (shape is not null)
                {
                    if (!TryBuildValueModel(generic.TypeArguments[0], inProgress, out var elem, reason) || elem is null)
                        return false;

                    model = OutboxValueModel.Collection(localTypeName, elem, shape.Value);
                    return true;
                }

                if (IsOneOf(definition, _dictionaryDefs))
                {
                    if (generic.TypeArguments[0].SpecialType != SpecialType.System_String)
                    {
                        reason.Append($"dictionary '{nonNullableTypeName}' is not supported: its keys must be strings, which is what a JSON object's property names are. ");
                        return false;
                    }

                    if (!TryBuildValueModel(generic.TypeArguments[1], inProgress, out var value, reason) || value is null)
                        return false;

                    model = OutboxValueModel.Dictionary(localTypeName, value);
                    return true;
                }
            }

            if (ImplementsEnumerableOfT(underlying))
            {
                reason.Append($"collection type '{nonNullableTypeName}' is not supported; a payload member is {SupportedShapes}. ");
                return false;
            }

            if (underlying is INamedTypeSymbol obj && IsEligibleNestedObject(obj))
            {
                if (!TryBuildObjectModel(obj, inProgress, out var nested, reason) || nested is null)
                    return false;

                model = OutboxValueModel.Object(localTypeName, nested, isNullableValueType, obj.IsReferenceType);
                return true;
            }

            reason.Append($"type '{nonNullableTypeName}' is not supported; a payload member is {SupportedShapes}. ");
            return false;
        }

        private static bool IsOneOf(INamedTypeSymbol definition, INamedTypeSymbol?[] definitions)
            => definitions.Any(d => d is not null && SymbolEqualityComparer.Default.Equals(d, definition));

        // Only when every constructor the reader might pick sets the required members itself: the check runs before a
        // constructor is selected, so one plain constructor next to a [SetsRequiredMembers] one does not count.
        private bool HasSetsRequiredMembersConstructor(INamedTypeSymbol type)
        {
            var accessible = type.InstanceConstructors.Where(c => GeneratedCodeAccessibility.IsAccessible(c, _compilation)).ToArray();
            return accessible.Length > 0 && accessible.All(c => c.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"));
        }

        private static IEnumerable<IFieldSymbol> RequiredFields(INamedTypeSymbol type)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
                foreach (var field in current.GetMembers().OfType<IFieldSymbol>())
                    if (field.IsRequired)
                        yield return field;
        }

        // The constructor System.Text.Json would use: the one marked [JsonConstructor]; else the parameterless one when
        // every member can be set after it; else the one with the most parameters that all map onto members.
        private bool TrySelectConstructor(
            INamedTypeSymbol type,
            OutboxMemberModel[] members,
            out string constructorExpression,
            StringBuilder reason)
        {
            constructorExpression = string.Empty;
            var typeName = type.ToDisplayString(Fq);

            var marked = _jsonConstructor is null
                ? Array.Empty<IMethodSymbol>()
                : type.InstanceConstructors
                    .Where(c => c.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonConstructor)))
                    .ToArray();
            if (marked.Length > 1)
            {
                reason.Append("more than one constructor is marked [JsonConstructor]. ");
                return false;
            }

            if (marked.Length == 1)
            {
                var constructor = marked[0];
                if (!GeneratedCodeAccessibility.IsAccessible(constructor, _compilation))
                {
                    reason.Append("its [JsonConstructor] constructor is not accessible to generated code. ");
                    return false;
                }

                if (constructor.Parameters.Length == 0)
                    return TryUseParameterless(typeName, members, out constructorExpression, reason);

                if (!TryMapConstructor(members, constructor, out var parameters, out var unmapped))
                {
                    reason.Append($"parameter '{unmapped}' of its [JsonConstructor] constructor matches no serializable property by name and type. ");
                    return false;
                }

                return TryUseConstructor(typeName, members, parameters, out constructorExpression, reason);
            }

            var parameterlessCtor = type.InstanceConstructors.FirstOrDefault(c =>
                c.Parameters.Length == 0 && GeneratedCodeAccessibility.IsAccessible(c, _compilation));

            if (parameterlessCtor is not null && members.All(m => m.CanInitialize))
            {
                constructorExpression = $"new {typeName}()";
                return true;
            }

            var candidates = type.InstanceConstructors
                .Where(c => c.Parameters.Length > 0 && GeneratedCodeAccessibility.IsAccessible(c, _compilation))
                .Select(c => (Ctor: c, Map: TryMapConstructor(members, c, out var map, out _) ? map : null))
                .Where(x => x.Map is not null)
                .Select(x => (x.Ctor, Map: x.Map!))
                .OrderByDescending(x => x.Ctor.Parameters.Length)
                .ToArray();

            if (candidates.Length == 0)
            {
                reason.Append("no accessible parameterless constructor and no accessible constructor with parameters matching its serializable properties. ");
                return false;
            }

            var bestParamCount = candidates[0].Ctor.Parameters.Length;
            var best = candidates.Where(c => c.Ctor.Parameters.Length == bestParamCount).ToArray();
            if (best.Length != 1)
            {
                reason.Append("multiple eligible constructors found; mark the one to use with [JsonConstructor] (or add a parameterless constructor). ");
                return false;
            }

            return TryUseConstructor(typeName, members, best[0].Map, out constructorExpression, reason);
        }

        private static bool TryUseParameterless(string typeName, OutboxMemberModel[] members, out string constructorExpression, StringBuilder reason)
        {
            constructorExpression = $"new {typeName}()";
            var notSettable = members.FirstOrDefault(m => !m.CanInitialize);
            if (notSettable is null) return true;

            reason.Append($"property '{notSettable.PropertyName}' is not settable and is not provided by the selected constructor. ");
            return false;
        }

        private static bool TryUseConstructor(
            string typeName,
            OutboxMemberModel[] members,
            (int Member, string? Default)[] parameters,
            out string constructorExpression,
            StringBuilder reason)
        {
            constructorExpression = string.Empty;
            var ctorLocalNames = new string[parameters.Length];
            for (var p = 0; p < parameters.Length; p++)
            {
                var (index, defaultValue) = parameters[p];
                members[index] = members[index] with { IsConstructorParameter = true, DefaultValueExpression = defaultValue };
                ctorLocalNames[p] = members[index].LocalName;
            }

            foreach (var member in members)
            {
                if (member.CanInitialize || member.IsConstructorParameter) continue;
                reason.Append($"property '{member.PropertyName}' is not settable and is not provided by the selected constructor. ");
                return false;
            }

            constructorExpression = $"new {typeName}({string.Join(", ", ctorLocalNames)})";
            return true;
        }

        // Each parameter maps onto the member of the same name (JSON or property name, ignoring case) and type. A
        // parameter with an explicit default carries it, so a payload that lacks the member passes what the constructor
        // would have used.
        private static bool TryMapConstructor(
            OutboxMemberModel[] members,
            IMethodSymbol ctor,
            out (int Member, string? Default)[] parameters,
            out string? unmapped)
        {
            parameters = Array.Empty<(int, string?)>();
            unmapped = null;
            if (ctor.Parameters.Length == 0) return false;

            var map = new (int Member, string? Default)[ctor.Parameters.Length];

            for (var i = 0; i < ctor.Parameters.Length; i++)
            {
                var param = ctor.Parameters[i];
                var paramTypeName = param.Type.ToDisplayString(Fq);
                if (paramTypeName.EndsWith("?", StringComparison.Ordinal))
                    paramTypeName = paramTypeName.Substring(0, paramTypeName.Length - 1);

                var found = -1;
                for (var p = 0; p < members.Length; p++)
                {
                    var member = members[p];
                    if (!string.Equals(member.JsonName, param.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(member.PropertyName, param.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var memberTypeName = member.Value.LocalTypeName;
                    if (memberTypeName.EndsWith("?", StringComparison.Ordinal))
                        memberTypeName = memberTypeName.Substring(0, memberTypeName.Length - 1);

                    if (!string.Equals(memberTypeName, paramTypeName, StringComparison.Ordinal))
                        continue;

                    found = p;
                    break;
                }

                if (found < 0)
                {
                    unmapped = param.Name;
                    return false;
                }

                map[i] = (found, param.HasExplicitDefaultValue ? RenderParameterDefault(param) : null);
            }

            if (map.Select(m => m.Member).Distinct().Count() != map.Length) return false;

            parameters = map;
            return true;
        }

        private static string RenderParameterDefault(IParameterSymbol parameter)
        {
            var type = parameter.Type;
            if (parameter.ExplicitDefaultValue is not { } value)
                return $"default({type.ToDisplayString(Fq)})";

            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];
            return RenderConstant(value, type);
        }

        private bool TryGetScalarKind(ITypeSymbol type, out OutboxScalarKind kind)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_String: kind = OutboxScalarKind.String; return true;
                case SpecialType.System_Boolean: kind = OutboxScalarKind.Boolean; return true;
                case SpecialType.System_Byte: kind = OutboxScalarKind.Byte; return true;
                case SpecialType.System_SByte: kind = OutboxScalarKind.SByte; return true;
                case SpecialType.System_Int16: kind = OutboxScalarKind.Int16; return true;
                case SpecialType.System_UInt16: kind = OutboxScalarKind.UInt16; return true;
                case SpecialType.System_Int32: kind = OutboxScalarKind.Int32; return true;
                case SpecialType.System_UInt32: kind = OutboxScalarKind.UInt32; return true;
                case SpecialType.System_Int64: kind = OutboxScalarKind.Int64; return true;
                case SpecialType.System_UInt64: kind = OutboxScalarKind.UInt64; return true;
                case SpecialType.System_Single: kind = OutboxScalarKind.Single; return true;
                case SpecialType.System_Double: kind = OutboxScalarKind.Double; return true;
                case SpecialType.System_Decimal: kind = OutboxScalarKind.Decimal; return true;
                case SpecialType.System_Char: kind = OutboxScalarKind.Char; return true;
            }

            if (Is(type, _guid)) { kind = OutboxScalarKind.Guid; return true; }
            if (Is(type, _dateTime)) { kind = OutboxScalarKind.DateTime; return true; }
            if (Is(type, _dateTimeOffset)) { kind = OutboxScalarKind.DateTimeOffset; return true; }
            if (Is(type, _timeSpan)) { kind = OutboxScalarKind.TimeSpan; return true; }
            if (Is(type, _dateOnly)) { kind = OutboxScalarKind.DateOnly; return true; }
            if (Is(type, _timeOnly)) { kind = OutboxScalarKind.TimeOnly; return true; }
            if (Is(type, _uri)) { kind = OutboxScalarKind.Uri; return true; }

            kind = default;
            return false;

            static bool Is(ITypeSymbol type, INamedTypeSymbol? scalar)
                => scalar is not null && SymbolEqualityComparer.Default.Equals(type, scalar);
        }

        private bool ImplementsEnumerableOfT(ITypeSymbol type)
        {
            if (_ienumerableOfT is null) return false;
            if (type is INamedTypeSymbol nt && nt.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(nt.OriginalDefinition, _ienumerableOfT))
                return true;
            return type.AllInterfaces.Any(i =>
                i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _ienumerableOfT));
        }

        private bool IsEligibleNestedObject(INamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
            if (type.IsAbstract) return false;
            if (type.IsTupleType) return false;
            if (type.SpecialType == SpecialType.System_Object) return false;
            if (!GeneratedCodeAccessibility.IsAccessible(type, _compilation)) return false;

            var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
                ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal))
                return false;

            return true;
        }
    }
}

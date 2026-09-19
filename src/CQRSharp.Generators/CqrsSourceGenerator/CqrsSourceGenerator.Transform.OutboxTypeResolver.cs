using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    /// <summary>
    ///     Builds the recursive outbox object/value graph for a notification type. Runs in the transform (it needs
    ///     symbols); the emitter consumes the resulting equatable records. STJ source-gen cannot be used here — see
    ///     <c>CqrsSourceGenerator.OutboxSerialization.cs</c>.
    /// </summary>
    private sealed class OutboxTypeResolver
    {
        private readonly INamedTypeSymbol? _guid;
        private readonly INamedTypeSymbol? _dateTime;
        private readonly INamedTypeSymbol? _dateTimeOffset;
        private readonly INamedTypeSymbol? _timeSpan;
        private readonly INamedTypeSymbol? _nullable;
        private readonly INamedTypeSymbol? _jsonPropertyName;
        private readonly INamedTypeSymbol? _jsonIgnore;
        private readonly INamedTypeSymbol? _ienumerableOfT;
        private readonly INamedTypeSymbol?[] _listLikeDefs;

        private readonly Dictionary<ITypeSymbol, OutboxObjectModel> _objectCache =
            new(SymbolEqualityComparer.Default);

        public OutboxTypeResolver(Compilation compilation)
        {
            _guid = compilation.GetTypeByMetadataName("System.Guid");
            _dateTime = compilation.GetTypeByMetadataName("System.DateTime");
            _dateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
            _timeSpan = compilation.GetTypeByMetadataName("System.TimeSpan");
            _nullable = compilation.GetTypeByMetadataName("System.Nullable`1");
            _jsonPropertyName = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
            _jsonIgnore = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
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
                var properties = GetDeclaredAndInheritedProperties(type)
                    .Where(p =>
                        !p.IsStatic &&
                        !p.IsIndexer &&
                        p.GetMethod is not null &&
                        IsAccessibleFromGeneratedCode(p.GetMethod) &&
                        p.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
                    .Where(p =>
                        _jsonIgnore is null ||
                        !p.GetAttributes().Any(a => a.AttributeClass is not null &&
                                                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonIgnore)))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();

                var members = new OutboxMemberModel[properties.Length];

                for (var i = 0; i < properties.Length; i++)
                {
                    var property = properties[i];
                    var propTypeName = property.Type.ToDisplayString(Fq);

                    if (!TryBuildValueModel(property.Type, inProgress, out var value, reason) || value is null)
                    {
                        reason.Append($"Unsupported property '{property.Name}' of type '{propTypeName}'. ");
                        return false;
                    }

                    var canInitialize = property.SetMethod is not null && IsAccessibleFromGeneratedCode(property.SetMethod);

                    members[i] = new OutboxMemberModel(
                        property.Name,
                        GetJsonPropertyName(property, _jsonPropertyName),
                        "__" + property.Name,
                        value,
                        canInitialize);
                }

                if (!TrySelectConstructor(type, members, out var constructorExpression, reason))
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
            var isNullableReferenceType = !isNullableValueType && type.NullableAnnotation == NullableAnnotation.Annotated;

            if (TryGetScalarKind(underlying, out var scalarKind))
            {
                model = OutboxValueModel.Scalar(scalarKind, localTypeName, nonNullableTypeName, isNullableValueType, isNullableReferenceType);
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

                model = OutboxValueModel.Collection(localTypeName, elem, true);
                return true;
            }

            if (underlying is INamedTypeSymbol generic &&
                generic.IsGenericType &&
                generic.TypeArguments.Length == 1 &&
                _listLikeDefs.Any(d => d is not null && SymbolEqualityComparer.Default.Equals(d, generic.OriginalDefinition)))
            {
                if (!TryBuildValueModel(generic.TypeArguments[0], inProgress, out var elem, reason) || elem is null)
                    return false;

                model = OutboxValueModel.Collection(localTypeName, elem, false);
                return true;
            }

            if (ImplementsEnumerableOfT(underlying))
            {
                reason.Append($"collection type '{nonNullableTypeName}' is not supported (only arrays and List/IList/IReadOnlyList/ICollection/IReadOnlyCollection/IEnumerable of a supported element type). ");
                return false;
            }

            if (underlying is INamedTypeSymbol obj && IsEligibleNestedObject(obj))
            {
                if (!TryBuildObjectModel(obj, inProgress, out var nested, reason) || nested is null)
                    return false;

                model = OutboxValueModel.Object(localTypeName, nested, isNullableValueType, obj.IsReferenceType);
                return true;
            }

            reason.Append($"type '{nonNullableTypeName}' is not a supported scalar, enum, array, list, or serializable object. ");
            return false;
        }

        private bool TrySelectConstructor(
            INamedTypeSymbol type,
            OutboxMemberModel[] members,
            out string constructorExpression,
            StringBuilder reason)
        {
            constructorExpression = string.Empty;
            var typeName = type.ToDisplayString(Fq);

            var parameterlessCtor = type.InstanceConstructors.FirstOrDefault(c =>
                c.Parameters.Length == 0 && IsAccessibleFromGeneratedCode(c));

            var hasNonInitializable = members.Any(m => !m.CanInitialize);

            if (parameterlessCtor is not null && !hasNonInitializable)
            {
                constructorExpression = $"new {typeName}()";
                return true;
            }

            var candidates = type.InstanceConstructors
                .Where(c => c.Parameters.Length > 0 && IsAccessibleFromGeneratedCode(c))
                .Select(c => (Ctor: c, Map: TryMapConstructor(members, c, out var map) ? map : null))
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
                reason.Append("multiple eligible constructors found; ensure a single unambiguous constructor (or add a parameterless constructor). ");
                return false;
            }

            var ctorIndices = best[0].Map;
            var ctorLocalNames = new string[ctorIndices.Length];
            for (var p = 0; p < ctorIndices.Length; p++)
            {
                var index = ctorIndices[p];
                members[index] = members[index] with { IsConstructorParameter = true };
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

        private static bool TryMapConstructor(OutboxMemberModel[] members, IMethodSymbol ctor, out int[] propertyIndices)
        {
            propertyIndices = Array.Empty<int>();
            if (ctor.Parameters.Length == 0) return false;

            var map = new int[ctor.Parameters.Length];

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

                if (found < 0) return false;
                map[i] = found;
            }

            if (map.Distinct().Count() != map.Length) return false;

            propertyIndices = map;
            return true;
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
            }

            if (_guid is not null && SymbolEqualityComparer.Default.Equals(type, _guid)) { kind = OutboxScalarKind.Guid; return true; }
            if (_dateTime is not null && SymbolEqualityComparer.Default.Equals(type, _dateTime)) { kind = OutboxScalarKind.DateTime; return true; }
            if (_dateTimeOffset is not null && SymbolEqualityComparer.Default.Equals(type, _dateTimeOffset)) { kind = OutboxScalarKind.DateTimeOffset; return true; }
            if (_timeSpan is not null && SymbolEqualityComparer.Default.Equals(type, _timeSpan)) { kind = OutboxScalarKind.TimeSpan; return true; }

            kind = default;
            return false;
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

        private static bool IsEligibleNestedObject(INamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
            if (type.IsAbstract) return false;
            if (type.IsTupleType) return false;
            if (type.SpecialType == SpecialType.System_Object) return false;
            if (!IsAccessibleFromGeneratedCode(type)) return false;

            var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
                ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal))
                return false;

            return true;
        }
    }
}

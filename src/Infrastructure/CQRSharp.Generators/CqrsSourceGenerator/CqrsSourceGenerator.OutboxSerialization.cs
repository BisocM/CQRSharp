using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Abstractions.SourceGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.Cqrs;

public sealed partial class CqrsSourceGenerator
{
    private static readonly DiagnosticDescriptor OutboxNotificationNotAotJsonSerializableDiagnostic = new(
        "CQRGEN005",
        "Outbox notification is not AOT-JSON serializable",
        "Notification '{0}' cannot be source-generated for AOT-safe outbox JSON: {1}. Either change the notification shape or register a custom INotificationSerializer.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private enum OutboxJsonValueKind
    {
        String,
        Guid,
        Boolean,
        Int32,
        Int64,
        Double,
        Decimal,
        DateTime,
        DateTimeOffset,
        Enum
    }

    private sealed class OutboxPropertyInfo
    {
        public OutboxPropertyInfo(
            string propertyName,
            string jsonName,
            string localName,
            string typeName,
            OutboxJsonValueKind valueKind,
            bool isNullableValueType,
            bool isNullableReferenceType,
            string? enumUnderlyingTypeName,
            bool isConstructorParameter,
            bool canInitialize)
        {
            PropertyName = propertyName;
            JsonName = jsonName;
            LocalName = localName;
            TypeName = typeName;
            ValueKind = valueKind;
            IsNullableValueType = isNullableValueType;
            IsNullableReferenceType = isNullableReferenceType;
            EnumUnderlyingTypeName = enumUnderlyingTypeName;
            IsConstructorParameter = isConstructorParameter;
            CanInitialize = canInitialize;
        }

        public string PropertyName { get; }
        public string JsonName { get; }
        public string LocalName { get; }
        public string TypeName { get; }
        public OutboxJsonValueKind ValueKind { get; }
        public bool IsNullableValueType { get; }
        public bool IsNullableReferenceType { get; }
        public string? EnumUnderlyingTypeName { get; }
        public bool IsConstructorParameter { get; }
        public bool CanInitialize { get; }
    }

    private sealed class StableNotificationInfo
    {
        public StableNotificationInfo(
            string stableName,
            string typeName,
            string deserializeMethodName,
            string serializeMethodName,
            string constructorExpression,
            OutboxPropertyInfo[] properties,
            string[] constructorLocalNames)
        {
            StableName = stableName;
            TypeName = typeName;
            DeserializeMethodName = deserializeMethodName;
            SerializeMethodName = serializeMethodName;
            ConstructorExpression = constructorExpression;
            Properties = properties;
            ConstructorLocalNames = constructorLocalNames;
        }

        public string StableName { get; }
        public string TypeName { get; }
        public string DeserializeMethodName { get; }
        public string SerializeMethodName { get; }
        public string ConstructorExpression { get; }
        public OutboxPropertyInfo[] Properties { get; }
        public string[] ConstructorLocalNames { get; }
    }

    private static bool IsAccessibleFromGeneratedCode(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return false;

        return IsAccessibleFromGeneratedCode(methodSymbol.ContainingType);
    }

    private static StableNotificationInfo[] CollectStableNotifications(
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol> candidateClasses,
        SourceProductionContext context)
    {
        var notificationSymbol = compilation.GetTypeByMetadataName(TypeStrings.INotification);
        var notificationNameAttributeSymbol = compilation.GetTypeByMetadataName(TypeStrings.NotificationNameAttribute);

        var jsonPropertyNameAttributeSymbol =
            compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
        var jsonIgnoreAttributeSymbol =
            compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");

        var guidSymbol = compilation.GetTypeByMetadataName("System.Guid");
        var dateTimeSymbol = compilation.GetTypeByMetadataName("System.DateTime");
        var dateTimeOffsetSymbol = compilation.GetTypeByMetadataName("System.DateTimeOffset");
        var nullableSymbol = compilation.GetTypeByMetadataName("System.Nullable`1");

        if (notificationSymbol is null || notificationNameAttributeSymbol is null)
            return Array.Empty<StableNotificationInfo>();

        var stableNotificationsWithPotentialDuplicates = candidateClasses
            .Where(c => c is { IsAbstract: false, IsGenericType: false } &&
                        IsAccessibleFromGeneratedCode(c) &&
                        c.AllInterfaces.Contains(notificationSymbol, SymbolEqualityComparer.Default))
            .Select(c =>
            {
                var attr = c.GetAttributes()
                    .FirstOrDefault(a =>
                        a.AttributeClass is not null &&
                        SymbolEqualityComparer.Default.Equals(a.AttributeClass, notificationNameAttributeSymbol));
                if (attr is null) return null;

                var arg = attr.ConstructorArguments.FirstOrDefault();
                var stableName = arg.Value as string;
                if (string.IsNullOrWhiteSpace(stableName)) return null;

                var typeName = c.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (!TryCreateStableNotificationInfo(
                        c,
                        stableName!,
                        typeName,
                        guidSymbol,
                        dateTimeSymbol,
                        dateTimeOffsetSymbol,
                        nullableSymbol,
                        jsonPropertyNameAttributeSymbol,
                        jsonIgnoreAttributeSymbol,
                        context,
                        out var info))
                    return null;

                return info;
            })
            .Where(x => x is not null)
            .Cast<StableNotificationInfo>()
            .ToArray();

        foreach (var group in stableNotificationsWithPotentialDuplicates
                     .GroupBy(n => n.StableName, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var types = string.Join(", ", group.Select(n => n.TypeName));
            context.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor(
                    "CQRGEN002",
                    "Duplicate NotificationName",
                    "Duplicate [NotificationName] '{0}' found on: {1}. Stable names must be unique for outbox serialization.",
                    "CQRSharp.Generators",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                Location.None,
                group.Key,
                types));
        }

        var stableNotifications = stableNotificationsWithPotentialDuplicates
            .GroupBy(n => n.StableName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(n => n.StableName, StringComparer.Ordinal)
            .ThenBy(n => n.TypeName, StringComparer.Ordinal)
            .ToArray();

        return stableNotifications;
    }

    private static bool TryCreateStableNotificationInfo(
        INamedTypeSymbol notificationType,
        string stableName,
        string typeName,
        INamedTypeSymbol? guidSymbol,
        INamedTypeSymbol? dateTimeSymbol,
        INamedTypeSymbol? dateTimeOffsetSymbol,
        INamedTypeSymbol? nullableSymbol,
        INamedTypeSymbol? jsonPropertyNameAttributeSymbol,
        INamedTypeSymbol? jsonIgnoreAttributeSymbol,
        SourceProductionContext context,
        out StableNotificationInfo info)
    {
        info = null!;

        var properties = notificationType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p =>
                !p.IsStatic &&
                !p.IsIndexer &&
                p.GetMethod is not null &&
                IsAccessibleFromGeneratedCode(p.GetMethod) &&
                p.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .Where(p =>
                jsonIgnoreAttributeSymbol is null ||
                !p.GetAttributes().Any(a => a.AttributeClass is not null &&
                                           SymbolEqualityComparer.Default.Equals(a.AttributeClass, jsonIgnoreAttributeSymbol)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        var propertyInfos = new OutboxPropertyInfo[properties.Length];
        var typeInfoErrors = new StringBuilder();

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];

            var jsonName = GetJsonPropertyName(property, jsonPropertyNameAttributeSymbol);
            var localName = "__" + property.Name;
            var propertyTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (!TryGetOutboxJsonValueKind(
                    property.Type,
                    guidSymbol,
                    dateTimeSymbol,
                    dateTimeOffsetSymbol,
                    nullableSymbol,
                    out var kind,
                    out var isNullableValueType,
                    out var isNullableReferenceType,
                    out var enumUnderlyingTypeName))
            {
                typeInfoErrors.Append($"Unsupported property '{property.Name}' of type '{propertyTypeName}'. ");
                continue;
            }

            var canInitialize = property.SetMethod is not null && IsAccessibleFromGeneratedCode(property.SetMethod);

            propertyInfos[i] = new OutboxPropertyInfo(
                property.Name,
                jsonName,
                localName,
                propertyTypeName,
                kind,
                isNullableValueType,
                isNullableReferenceType,
                enumUnderlyingTypeName,
                isConstructorParameter: false,
                canInitialize: canInitialize);
        }

        if (typeInfoErrors.Length > 0)
        {
            var location = notificationType.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                OutboxNotificationNotAotJsonSerializableDiagnostic,
                location,
                typeName,
                typeInfoErrors.ToString().Trim()));
            return false;
        }

        // Select a constructor: prefer parameterless if it can initialize all properties; otherwise pick a unique eligible parameterized constructor.
        var parameterlessCtor = notificationType.InstanceConstructors.FirstOrDefault(c =>
            c.Parameters.Length == 0 &&
            IsAccessibleFromGeneratedCode(c));

        IMethodSymbol? selectedCtor = null;
        int[]? ctorPropertyIndices = null;

        static bool HasNonInitializableProperties(OutboxPropertyInfo[] infos)
            => infos.Any(p => p is { CanInitialize: false });

        if (parameterlessCtor is not null && !HasNonInitializableProperties(propertyInfos))
        {
            selectedCtor = parameterlessCtor;
            ctorPropertyIndices = Array.Empty<int>();
        }
        else
        {
            var candidates = notificationType.InstanceConstructors
                .Where(c => c.Parameters.Length > 0 && IsAccessibleFromGeneratedCode(c))
                .Select(c => (Ctor: c, Map: TryMapConstructor(notificationType, propertyInfos, c, out var map) ? map : null))
                .Where(x => x.Map is not null)
                .Select(x => (x.Ctor, Map: x.Map!))
                .OrderByDescending(x => x.Ctor.Parameters.Length)
                .ToArray();

            if (candidates.Length == 0)
            {
                var location = notificationType.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    OutboxNotificationNotAotJsonSerializableDiagnostic,
                    location,
                    typeName,
                    "No accessible parameterless constructor and no accessible constructor with parameters matching its serializable properties."));
                return false;
            }

            var bestParamCount = candidates[0].Ctor.Parameters.Length;
            var best = candidates.Where(c => c.Ctor.Parameters.Length == bestParamCount).ToArray();
            if (best.Length != 1)
            {
                var location = notificationType.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    OutboxNotificationNotAotJsonSerializableDiagnostic,
                    location,
                    typeName,
                    "Multiple eligible constructors found. Ensure the notification has a single unambiguous constructor for deserialization (or add a parameterless constructor)."));
                return false;
            }

            selectedCtor = best[0].Ctor;
            ctorPropertyIndices = best[0].Map;
        }

        if (selectedCtor is null || ctorPropertyIndices is null)
            return false;

        // Mark constructor parameter properties.
        var ctorLocalNames = new string[ctorPropertyIndices.Length];
        for (var p = 0; p < ctorPropertyIndices.Length; p++)
        {
            var index = ctorPropertyIndices[p];
            var existing = propertyInfos[index];
            propertyInfos[index] = new OutboxPropertyInfo(
                existing.PropertyName,
                existing.JsonName,
                existing.LocalName,
                existing.TypeName,
                existing.ValueKind,
                existing.IsNullableValueType,
                existing.IsNullableReferenceType,
                existing.EnumUnderlyingTypeName,
                isConstructorParameter: true,
                canInitialize: existing.CanInitialize);
            ctorLocalNames[p] = existing.LocalName;
        }

        // Validate that every non-initializable property is supplied by the constructor.
        foreach (var prop in propertyInfos)
        {
            if (prop.CanInitialize) continue;
            if (prop.IsConstructorParameter) continue;

            var location = notificationType.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                OutboxNotificationNotAotJsonSerializableDiagnostic,
                location,
                typeName,
                $"Property '{prop.PropertyName}' is not settable and is not provided by the selected constructor."));
            return false;
        }

        var methodSuffix = CreateStableNameIdentifierSuffix(stableName);
        var serializeMethodName = "Serialize_" + methodSuffix;
        var deserializeMethodName = "Deserialize_" + methodSuffix;

        var constructorExpression = selectedCtor.Parameters.Length == 0
            ? $"new {typeName}()"
            : $"new {typeName}({string.Join(", ", ctorLocalNames)})";

        info = new StableNotificationInfo(
            stableName,
            typeName,
            deserializeMethodName,
            serializeMethodName,
            constructorExpression,
            propertyInfos,
            ctorLocalNames);

        return true;
    }

    private static bool TryMapConstructor(
        INamedTypeSymbol notificationType,
        OutboxPropertyInfo[] properties,
        IMethodSymbol ctor,
        out int[] propertyIndices)
    {
        propertyIndices = Array.Empty<int>();

        if (ctor.Parameters.Length == 0) return false;

        // Map by parameter name -> property jsonName or property name (case-insensitive).
        var map = new int[ctor.Parameters.Length];

        for (var i = 0; i < ctor.Parameters.Length; i++)
        {
            var param = ctor.Parameters[i];
            var paramTypeName = param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (paramTypeName.EndsWith("?", StringComparison.Ordinal))
                paramTypeName = paramTypeName.Substring(0, paramTypeName.Length - 1);

            var found = -1;
            for (var p = 0; p < properties.Length; p++)
            {
                var prop = properties[p];
                if (!string.Equals(prop.JsonName, param.Name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(prop.PropertyName, param.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var propertyTypeName = prop.TypeName;
                if (propertyTypeName.EndsWith("?", StringComparison.Ordinal))
                    propertyTypeName = propertyTypeName.Substring(0, propertyTypeName.Length - 1);

                if (!string.Equals(propertyTypeName, paramTypeName, StringComparison.Ordinal))
                    continue;

                found = p;
                break;
            }

            if (found < 0)
                return false;

            map[i] = found;
        }

        // Ensure uniqueness: no two parameters bind to the same property.
        if (map.Distinct().Count() != map.Length) return false;

        propertyIndices = map;
        return true;
    }

    private static bool TryGetOutboxJsonValueKind(
        ITypeSymbol typeSymbol,
        INamedTypeSymbol? guidSymbol,
        INamedTypeSymbol? dateTimeSymbol,
        INamedTypeSymbol? dateTimeOffsetSymbol,
        INamedTypeSymbol? nullableSymbol,
        out OutboxJsonValueKind kind,
        out bool isNullableValueType,
        out bool isNullableReferenceType,
        out string? enumUnderlyingTypeName)
    {
        enumUnderlyingTypeName = null;
        isNullableValueType = false;
        isNullableReferenceType = false;

        // Unwrap Nullable<T> for value types.
        if (nullableSymbol is not null &&
            typeSymbol is INamedTypeSymbol named &&
            named.IsGenericType &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, nullableSymbol) &&
            named.TypeArguments.Length == 1)
        {
            isNullableValueType = true;
            typeSymbol = named.TypeArguments[0];
        }

        if (typeSymbol.SpecialType == SpecialType.System_String)
        {
            kind = OutboxJsonValueKind.String;
            isNullableReferenceType = typeSymbol.NullableAnnotation == NullableAnnotation.Annotated;
            return true;
        }

        if (guidSymbol is not null && SymbolEqualityComparer.Default.Equals(typeSymbol, guidSymbol))
        {
            kind = OutboxJsonValueKind.Guid;
            return true;
        }

        if (dateTimeSymbol is not null && SymbolEqualityComparer.Default.Equals(typeSymbol, dateTimeSymbol))
        {
            kind = OutboxJsonValueKind.DateTime;
            return true;
        }

        if (dateTimeOffsetSymbol is not null && SymbolEqualityComparer.Default.Equals(typeSymbol, dateTimeOffsetSymbol))
        {
            kind = OutboxJsonValueKind.DateTimeOffset;
            return true;
        }

        switch (typeSymbol.SpecialType)
        {
            case SpecialType.System_Boolean:
                kind = OutboxJsonValueKind.Boolean;
                return true;
            case SpecialType.System_Int32:
                kind = OutboxJsonValueKind.Int32;
                return true;
            case SpecialType.System_Int64:
                kind = OutboxJsonValueKind.Int64;
                return true;
            case SpecialType.System_Double:
                kind = OutboxJsonValueKind.Double;
                return true;
            case SpecialType.System_Decimal:
                kind = OutboxJsonValueKind.Decimal;
                return true;
        }

        if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType)
        {
            kind = OutboxJsonValueKind.Enum;
            enumUnderlyingTypeName = enumType.EnumUnderlyingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return enumUnderlyingTypeName is not null;
        }

        kind = default;
        return false;
    }

    private static string GetJsonPropertyName(IPropertySymbol property, INamedTypeSymbol? jsonPropertyNameAttributeSymbol)
    {
        if (jsonPropertyNameAttributeSymbol is not null)
        {
            var attr = property.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is not null &&
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, jsonPropertyNameAttributeSymbol));
            if (attr is not null)
            {
                var arg = attr.ConstructorArguments.FirstOrDefault();
                if (arg.Value is string name && !string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return ToCamelCase(property.Name);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (!char.IsUpper(name[0])) return name;

        var chars = name.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    private static string CreateStableNameIdentifierSuffix(string stableName)
    {
        var hash = StableNameHash(stableName);

        var sb = new StringBuilder();
        sb.Append("N_");

        var maxLen = Math.Min(stableName.Length, 40);
        for (var i = 0; i < maxLen; i++)
        {
            var c = stableName[i];
            if ((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }

        sb.Append('_');
        sb.Append(hash.ToString("X8"));
        return sb.ToString();
    }

    private static uint StableNameHash(string stableName)
    {
        // FNV-1a 32-bit
        unchecked
        {
            var hash = 2166136261u;
            for (var i = 0; i < stableName.Length; i++)
            {
                hash ^= stableName[i];
                hash *= 16777619u;
            }

            return hash;
        }
    }

    private static string GenerateOutboxNotificationSerializer(StableNotificationInfo[] stableNotifications)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using CQRSharp.Abstractions.Data.Interfaces.Notifications;");
        sb.AppendLine();
        sb.AppendLine("namespace CQRSharp.Core.Serialization.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal sealed class GeneratedOutboxNotificationSerializer : INotificationSerializer, IStableNotificationNameProvider");
        sb.AppendLine("    {");
        sb.AppendLine("        public byte[] Serialize(INotification notification)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(notification);");
        sb.AppendLine();
        sb.AppendLine("            return notification switch");
        sb.AppendLine("            {");
        foreach (var notification in stableNotifications)
            sb.AppendLine($"                {notification.TypeName} n => {notification.SerializeMethodName}(n),");
        sb.AppendLine(
            "                _ => throw new InvalidOperationException($\"Notification type '{notification.GetType().FullName}' is not registered for outbox serialization. Add [NotificationName] or register a custom INotificationSerializer.\")");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public INotification? Deserialize(string notificationName, byte[] payload)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (string.IsNullOrWhiteSpace(notificationName)) return null;");
        sb.AppendLine("            if (payload is null || payload.Length == 0) return null;");
        sb.AppendLine();
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("            return notificationName switch");
        sb.AppendLine("            {");
        foreach (var notification in stableNotifications)
        {
            var nameLiteral = SymbolDisplay.FormatLiteral(notification.StableName, quote: true);
            sb.AppendLine(
                $"                {nameLiteral} => {notification.DeserializeMethodName}(payload),");
        }

        sb.AppendLine("                _ => null");
        sb.AppendLine("            };");
        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine("                return null;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public string GetNotificationName(Type notificationType)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(notificationType);");
        sb.AppendLine("            return GetStableName(notificationType) ?? notificationType.FullName ?? notificationType.Name;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public bool TryGetStableName(Type notificationType, out string stableName)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(notificationType);");
        sb.AppendLine("            stableName = GetStableName(notificationType) ?? string.Empty;");
        sb.AppendLine("            return stableName.Length > 0;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private static string? GetStableName(Type notificationType)");
        sb.AppendLine("        {");
        foreach (var notification in stableNotifications)
        {
            var nameLiteral = SymbolDisplay.FormatLiteral(notification.StableName, quote: true);
            sb.AppendLine($"            if (notificationType == typeof({notification.TypeName})) return {nameLiteral};");
        }
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine();

        foreach (var notification in stableNotifications)
        {
            sb.AppendLine($"        private static byte[] {notification.SerializeMethodName}({notification.TypeName} notification)");
            sb.AppendLine("        {");
            sb.AppendLine("            var buffer = new ArrayBufferWriter<byte>();");
            sb.AppendLine("            using (var writer = new Utf8JsonWriter(buffer))");
            sb.AppendLine("            {");
            sb.AppendLine("                writer.WriteStartObject();");

            foreach (var prop in notification.Properties)
            {
                var jsonNameLiteral = SymbolDisplay.FormatLiteral(prop.JsonName, quote: true);
                var access = $"notification.{prop.PropertyName}";

                if (prop.IsNullableValueType)
                {
                    sb.AppendLine($"                if ({access}.HasValue)");
                    sb.AppendLine("                {");
                    GenerateWriteNonNullValue(sb, prop, jsonNameLiteral, $"{access}.Value");
                    sb.AppendLine("                }");
                    sb.AppendLine("                else");
                    sb.AppendLine("                {");
                    sb.AppendLine($"                    writer.WriteNull({jsonNameLiteral});");
                    sb.AppendLine("                }");
                }
                else
                {
                    GenerateWriteNonNullValue(sb, prop, jsonNameLiteral, access);
                }
            }

            sb.AppendLine("                writer.WriteEndObject();");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            var result = new byte[buffer.WrittenCount];");
            sb.AppendLine("            buffer.WrittenSpan.CopyTo(result);");
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine($"        private static {notification.TypeName} {notification.DeserializeMethodName}(byte[] payload)");
            sb.AppendLine("        {");

            foreach (var prop in notification.Properties)
                GenerateLocalDeclaration(sb, prop);

            sb.AppendLine();
            sb.AppendLine("            var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);");
            sb.AppendLine("            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)");
            sb.AppendLine("                throw new JsonException(\"Expected start of JSON object.\");");
            sb.AppendLine();
            sb.AppendLine("            while (reader.Read())");
            sb.AppendLine("            {");
            sb.AppendLine("                if (reader.TokenType == JsonTokenType.EndObject) break;");
            sb.AppendLine("                if (reader.TokenType != JsonTokenType.PropertyName) { reader.Skip(); continue; }");
            sb.AppendLine();
            sb.AppendLine("                var propName = reader.GetString();");
            sb.AppendLine("                if (!reader.Read()) break;");
            sb.AppendLine();
            sb.AppendLine("                switch (propName)");
            sb.AppendLine("                {");

            foreach (var prop in notification.Properties)
            {
                var jsonNameLiteral = SymbolDisplay.FormatLiteral(prop.JsonName, quote: true);
                var propertyNameLiteral = SymbolDisplay.FormatLiteral(prop.PropertyName, quote: true);

                sb.AppendLine($"                    case {jsonNameLiteral}:");
                if (!string.Equals(prop.JsonName, prop.PropertyName, StringComparison.Ordinal))
                    sb.AppendLine($"                    case {propertyNameLiteral}:");

                GenerateReadAssignment(sb, prop);
                sb.AppendLine("                        break;");
            }

            sb.AppendLine("                    default:");
            sb.AppendLine("                        reader.Skip();");
            sb.AppendLine("                        break;");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine();

            var initAssignments = notification.Properties
                .Where(p => p.CanInitialize && !p.IsConstructorParameter)
                .Select(p => $"{p.PropertyName} = {p.LocalName}")
                .ToArray();

            if (initAssignments.Length == 0)
            {
                sb.AppendLine($"            return {notification.ConstructorExpression};");
            }
            else
            {
                sb.AppendLine($"            return {notification.ConstructorExpression}");
                sb.AppendLine("            {");
                for (var i = 0; i < initAssignments.Length; i++)
                {
                    var comma = i == initAssignments.Length - 1 ? string.Empty : ",";
                    sb.AppendLine($"                {initAssignments[i]}{comma}");
                }
                sb.AppendLine("            };");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void GenerateLocalDeclaration(StringBuilder sb, OutboxPropertyInfo prop)
    {
        var typeName = prop.TypeName;
        var local = prop.LocalName;

        if (prop.ValueKind == OutboxJsonValueKind.String)
        {
            var defaultValue = prop.IsNullableReferenceType ? "null" : "string.Empty";
            sb.AppendLine($"            {typeName} {local} = {defaultValue};");
            return;
        }

        sb.AppendLine($"            {typeName} {local} = default;");
    }

    private static void GenerateWriteNonNullValue(StringBuilder sb, OutboxPropertyInfo prop, string jsonNameLiteral, string valueExpression)
    {
        switch (prop.ValueKind)
        {
            case OutboxJsonValueKind.String:
                sb.AppendLine($"                writer.WriteString({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Guid:
                sb.AppendLine($"                writer.WriteString({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.DateTime:
                sb.AppendLine($"                writer.WriteString({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.DateTimeOffset:
                sb.AppendLine($"                writer.WriteString({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Boolean:
                sb.AppendLine($"                writer.WriteBoolean({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Int32:
                sb.AppendLine($"                writer.WriteNumber({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Int64:
                sb.AppendLine($"                writer.WriteNumber({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Double:
                sb.AppendLine($"                writer.WriteNumber({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Decimal:
                sb.AppendLine($"                writer.WriteNumber({jsonNameLiteral}, {valueExpression});");
                return;
            case OutboxJsonValueKind.Enum:
                var enumUnderlying = prop.EnumUnderlyingTypeName ?? "global::System.Int32";
                sb.AppendLine($"                writer.WriteNumber({jsonNameLiteral}, ({enumUnderlying}){valueExpression});");
                return;
            default:
                sb.AppendLine($"                throw new NotSupportedException(\"Unsupported outbox JSON value kind for '{prop.PropertyName}'.\");");
                return;
        }
    }

    private static void GenerateReadAssignment(StringBuilder sb, OutboxPropertyInfo prop)
    {
        var local = prop.LocalName;

        switch (prop.ValueKind)
        {
            case OutboxJsonValueKind.String:
                if (prop.IsNullableReferenceType)
                {
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();");
                }
                else
                {
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? string.Empty : (reader.GetString() ?? string.Empty);");
                }
                return;

            case OutboxJsonValueKind.Guid:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetGuid();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetGuid();");
                return;

            case OutboxJsonValueKind.DateTime:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTime();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetDateTime();");
                return;

            case OutboxJsonValueKind.DateTimeOffset:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetDateTimeOffset();");
                return;

            case OutboxJsonValueKind.Boolean:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetBoolean();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetBoolean();");
                return;

            case OutboxJsonValueKind.Int32:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetInt32();");
                return;

            case OutboxJsonValueKind.Int64:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt64();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetInt64();");
                return;

            case OutboxJsonValueKind.Double:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetDouble();");
                return;

            case OutboxJsonValueKind.Decimal:
                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : reader.GetDecimal();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : reader.GetDecimal();");
                return;

            case OutboxJsonValueKind.Enum:
                var enumUnderlying = prop.EnumUnderlyingTypeName ?? "global::System.Int32";
                var readMethod = enumUnderlying switch
                {
                    "global::System.Byte" => "GetByte",
                    "global::System.SByte" => "GetSByte",
                    "global::System.Int16" => "GetInt16",
                    "global::System.UInt16" => "GetUInt16",
                    "global::System.Int32" => "GetInt32",
                    "global::System.UInt32" => "GetUInt32",
                    "global::System.Int64" => "GetInt64",
                    "global::System.UInt64" => "GetUInt64",
                    _ => "GetInt32"
                };

                if (prop.IsNullableValueType)
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? null : ({GetNonNullableTypeName(prop)})reader.{readMethod}();");
                else
                    sb.AppendLine($"                        {local} = reader.TokenType == JsonTokenType.Null ? default : ({prop.TypeName})reader.{readMethod}();");
                return;

            default:
                sb.AppendLine($"                        throw new NotSupportedException(\"Unsupported outbox JSON value kind for '{prop.PropertyName}'.\");");
                return;
        }
    }

    private static string GetNonNullableTypeName(OutboxPropertyInfo prop)
    {
        if (!prop.IsNullableValueType) return prop.TypeName;

        const string prefix = "global::System.Nullable<";
        if (prop.TypeName.StartsWith(prefix, StringComparison.Ordinal) && prop.TypeName.EndsWith(">", StringComparison.Ordinal))
            return prop.TypeName.Substring(prefix.Length, prop.TypeName.Length - prefix.Length - 1);

        return prop.TypeName.TrimEnd('?');
    }
}

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Shared.Data.Attributes.Requests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.Types;

/// <summary>
///     A source generator that discovers types implementing ICommandHandler and IQueryHandler (both decorated with
///     [HandlerType]), and generates a static registry mapping request types to their corresponding invocation delegates.
/// </summary>
[Generator]
public sealed class HandlerRegistryGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        //Identify candidate classes (only classes are considered).
        var candidateClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol
            )
            .Where(symbol => symbol is not null);

        //Combine candidate classes with the current compilation.
        var compilationAndClasses = context.CompilationProvider.Combine(candidateClasses.Collect());

        //Register the source output.
        context.RegisterSourceOutput(compilationAndClasses, (spc, source) =>
        {
            var (compilation, classes) = source;

            try
            {
                var registrations = ProcessCandidateClasses(spc, compilation, classes);
                if (registrations.Length == 0)
                    //If no registrations, emit an informational diagnostic.
                    spc.ReportDiagnostic(Diagnostic.Create(
                        HandlerRegistryDiagnostics.NoHandlerRegistrationsFound,
                        Location.None
                    ));

                //Generate the registry source code.
                var sourceCode = GenerateRegistrySource(registrations);
                spc.AddSource("GeneratedHandlerRegistry.g.cs", SourceText.From(sourceCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                //In case of any unhandled exceptions, report a diagnostic.
                spc.ReportDiagnostic(Diagnostic.Create(
                    HandlerRegistryDiagnostics.UnhandledException,
                    Location.None,
                    ex.ToString()
                ));
            }
        });
    }

    /// <summary>
    ///     Processes all candidate classes, searching for ICommandHandler and IQueryHandler interfaces
    ///     decorated with [HandlerType], then prepares the relevant registration entries.
    /// </summary>
    private static ImmutableArray<HandlerRegistrationEntry> ProcessCandidateClasses(
        SourceProductionContext spc,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol?> classes)
    {
        var registrationsBuilder = ImmutableArray.CreateBuilder<HandlerRegistrationEntry>();

        //Lookup the HandlerType attribute.
        var handlerTypeAttrName = typeof(HandlerTypeAttribute).FullName!;
        var handlerTypeAttributeSymbol = compilation.GetTypeByMetadataName(handlerTypeAttrName);
        if (handlerTypeAttributeSymbol == null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                HandlerRegistryDiagnostics.MissingHandlerTypeAttribute,
                Location.None
            ));
            return registrationsBuilder.ToImmutable();
        }

        //Process each candidate class.
        foreach (var iface in from classSymbol in Enumerable.OfType<INamedTypeSymbol>(classes)
                 from iface in classSymbol.AllInterfaces
                 let hasHandlerTypeAttr = iface.GetAttributes()
                     .Any(attr =>
                         SymbolEqualityComparer.Default.Equals(attr.AttributeClass, handlerTypeAttributeSymbol))
                 where hasHandlerTypeAttr
                 select iface)
            //Process command handlers.
            if (iface.Name.StartsWith("ICommandHandler", StringComparison.Ordinal))
            {
                var genericArgs = iface.TypeArguments
                    .Select(ta => ta.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .ToArray();
                if (genericArgs.Length < 1)
                    continue; //Should not happen, but safety check.

                //The request type is the first generic argument.
                var requestType = genericArgs[0];
                //Build the full generic interface string.
                var handlerInterface =
                    $"global::CQRSharp.Shared.Data.Interfaces.Handlers.ICommandHandler<{string.Join(", ", genericArgs)}>";
                registrationsBuilder.Add(new HandlerRegistrationEntry(requestType, handlerInterface, false));
            }
            //Process query handlers.
            else if (iface.Name.StartsWith("IQueryHandler", StringComparison.Ordinal))
            {
                var genericArgs = iface.TypeArguments
                    .Select(ta => ta.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .ToArray();
                if (genericArgs.Length < 2)
                    continue; //Safety check.

                var requestType = genericArgs[0];
                var handlerInterface =
                    $"global::CQRSharp.Shared.Data.Interfaces.Handlers.IQueryHandler<{string.Join(", ", genericArgs)}>";
                registrationsBuilder.Add(new HandlerRegistrationEntry(requestType, handlerInterface, true));
            }

        return registrationsBuilder.ToImmutable();
    }

    /// <summary>
    ///     Generates the source code for the handler registry.
    /// </summary>
    /// <param name="registrations">A collection of handler registration entries.</param>
    /// <returns>The generated source code as a string.</returns>
    private static string GenerateRegistrySource(ImmutableArray<HandlerRegistrationEntry> registrations)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Concurrent;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using CQRSharp.Core.Caching.Handlers;");
        sb.AppendLine("using CQRSharp.Core.SourceGeneration;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace CQRSharp.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// A generated registry that maps request types to their handler invoker delegates.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public sealed class GeneratedHandlerRegistryRegistrar : IDataRegistrar");
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly Dictionary<Type, HandlerInvokerDelegate> _handlerMap;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine(
            "        /// Initializes a new instance of the <see cref=\"GeneratedHandlerRegistryRegistrar\"/> class.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public GeneratedHandlerRegistryRegistrar()");
        sb.AppendLine("        {");
        sb.AppendLine("            _handlerMap = new Dictionary<Type, HandlerInvokerDelegate>();");

        var distinctRegs = registrations.Distinct();
        foreach (var reg in distinctRegs)
        {
            sb.AppendLine();
            sb.AppendLine($"            //Registration for {reg.RequestType}");
            sb.AppendLine($"            _handlerMap[typeof({reg.RequestType})] = async (handler, request, ct) =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                var result = await (({reg.HandlerInterface})handler)");
            sb.AppendLine($"                    .Handle(({reg.RequestType})request, ct)");
            sb.AppendLine("                    .ConfigureAwait(false);");
            sb.AppendLine("                return (object)result;");
            sb.AppendLine("            });");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        /// <inheritdoc />");
        sb.AppendLine("        public void RegisterData(IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(_handlerMap));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(
            "    /// A module initializer that assigns the generated handler registry to the global Registrar.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static class GeneratedHandlerRegistryInitializer");
        sb.AppendLine("    {");
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        public static void Initialize()");
        sb.AppendLine("        {");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                Registrar.HandlerRegistryRegistrar = new GeneratedHandlerRegistryRegistrar();");
        sb.AppendLine("            }");
        sb.AppendLine("            catch (Exception ex)");
        sb.AppendLine("            {");
        sb.AppendLine(
            "                throw new InvalidOperationException(\"Failed to initialize GeneratedHandlerRegistry.\", ex);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    ///     Represents a handler registration entry.
    /// </summary>
    private sealed class HandlerRegistrationEntry(string requestType, string handlerInterface, bool isQuery)
        : IEquatable<HandlerRegistrationEntry>
    {
        public string RequestType { get; } = requestType;
        public string HandlerInterface { get; } = handlerInterface;
        public bool IsQuery { get; } = isQuery;

        public bool Equals(HandlerRegistrationEntry? other)
        {
            if (other is null) return false;
            return RequestType == other.RequestType &&
                   HandlerInterface == other.HandlerInterface &&
                   IsQuery == other.IsQuery;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as HandlerRegistrationEntry);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 23 + RequestType.GetHashCode();
                hash = hash * 23 + HandlerInterface.GetHashCode();
                hash = hash * 23 + IsQuery.GetHashCode();
                return hash;
            }
        }
    }

    /// <summary>
    ///     Holds all diagnostic descriptors for <see cref="HandlerRegistryGenerator" />.
    /// </summary>
    private static class HandlerRegistryDiagnostics
    {
        /// <summary>
        ///     Emitted when the HandlerType attribute cannot be found in the compilation.
        /// </summary>
        public static readonly DiagnosticDescriptor MissingHandlerTypeAttribute = new(
            "CQRHRG001",
            "Missing HandlerTypeAttribute",
            "Could not find the HandlerTypeAttribute. Ensure CQRSharp.Shared.Attributes is referenced.",
            "CQRSharp.Generators",
            DiagnosticSeverity.Error,
            true
        );

        /// <summary>
        ///     Emitted when no handler registrations are discovered.
        /// </summary>
        public static readonly DiagnosticDescriptor NoHandlerRegistrationsFound = new(
            "CQRHRG002",
            "No Handler Registrations Found",
            "No handler registrations were discovered. Ensure that your handlers implement interfaces decorated with [HandlerType].",
            "CQRSharp.Generators",
            DiagnosticSeverity.Info,
            true
        );

        /// <summary>
        ///     Emitted when any unhandled exception occurs in the generator.
        /// </summary>
        public static readonly DiagnosticDescriptor UnhandledException = new(
            "CQRHRG999",
            "Unhandled Exception in HandlerRegistryGenerator",
            "Unhandled exception: {0}",
            "CQRSharp.Generators",
            DiagnosticSeverity.Error,
            true
        );
    }
}
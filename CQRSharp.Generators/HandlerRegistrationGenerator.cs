using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Shared.Attributes; // Your marker attributes
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators
{
    [Generator]
    public sealed class HandlerRegistrationGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //Create a syntax provider that picks up any class/record declarations
            var candidateClasses = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                    transform: static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol
                )
                .Where(symbol => symbol is not null);

            //Combine with the Compilation
            var compilationAndTypes = context.CompilationProvider.Combine(candidateClasses.Collect());

            //Register the final step
            context.RegisterSourceOutput(compilationAndTypes, (spc, source) =>
            {
                var (compilation, types) = source;

                try
                {
                    //Attempt to fetch the symbol for our marker attribute.
                    var handlerTypeAttrSymbol = compilation.GetTypeByMetadataName(typeof(HandlerTypeAttribute).FullName);
                    if (handlerTypeAttrSymbol is null)
                    {
                        //If the user hasn't referenced the assembly that contains HandlerTypeAttribute,
                        //we can't proceed. We'll produce a diagnostic and stop.
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.MissingHandlerTypeAttribute,
                            location: null
                        ));
                        return;
                    }

                    //We'll gather discovered registrations
                    var registrationsBuilder = ImmutableArray.CreateBuilder<RegistrationInfo>();

                    foreach (var typeSymbol in types)
                    {
                        //For each class, see if it implements any interface that has [HandlerType(...)] on it
                        foreach (var iface in typeSymbol.AllInterfaces)
                        {
                            var handlerAttr = iface.GetAttributes()
                                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, handlerTypeAttrSymbol));

                            if (handlerAttr is null)
                            {
                                //Not a recognized handler interface
                                continue;
                            }

                            //If found, the interface is a "handler interface" with some HandlerKind
                            //We typically treat the first type argument as the "request" type
                            if (iface.TypeArguments.Length == 0)
                            {
                                //Possibly a pipeline behavior or something else that doesn't have type arguments
                                //We'll produce a warning diagnostic to let the user know
                                spc.ReportDiagnostic(Diagnostic.Create(
                                    Diagnostics.HandlerInterfaceNoTypeArgs,
                                    location: null,
                                    messageArgs: new object[] { iface.ToDisplayString() }
                                ));
                                continue;
                            }

                            //Extract the request type from the first generic argument
                            var requestTypeSymbol = iface.TypeArguments[0];
                            var requestTypeName = requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                            //Determine the HandlerKind from the attribute's constructor
                            var registrationKind = ExtractHandlerKind(handlerAttr);

                            registrationsBuilder.Add(new RegistrationInfo(
                                RequestTypeName: requestTypeName,
                                HandlerInterfaceName: iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                HandlerImplementationName: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                RegistrationKind: registrationKind
                            ));
                        }
                    }

                    var registrations = registrationsBuilder.ToImmutable();

                    //If we found none, that's not necessarily an error, but let's produce an informational diagnostic
                    if (registrations.Length == 0)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.NoHandlersFound,
                            location: null
                        ));
                    }

                    //Generate code
                    var sourceCode = GenerateSource(registrations);
                    spc.AddSource("HandlerRegistration.g.cs", SourceText.From(sourceCode, Encoding.UTF8));

                    // A success diagnostic
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.HandlerRegistrationSuccess,
                        location: null,
                        messageArgs: new object[] { registrations.Length }
                    ));
                }
                catch (Exception ex)
                {
                    // Report any unhandled exceptions
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.HandlerRegistrationException,
                        location: null,
                        messageArgs: new object[] { ex.Message }
                    ));
                }
            });
        }

        /// <summary>
        /// Produces the final source code for the HandlerRegistry class.
        /// </summary>
        private static string GenerateSource(ImmutableArray<RegistrationInfo> registrations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");

            sb.AppendLine();
            sb.AppendLine("namespace CQRSharp.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Automatically generated registry of handler classes.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class HandlerRegistry");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Registers all discovered handler classes into the IServiceCollection.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static void RegisterHandlers(IServiceCollection services)");
            sb.AppendLine("        {");
            foreach (var reg in registrations.Distinct())
            {
                sb.AppendLine($"            services.AddTransient(typeof({reg.HandlerInterfaceName}), typeof({reg.HandlerImplementationName})); // {reg.RegistrationKind}");
            }
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Maps each request type to its (HandlerInterface, RegistrationKind).");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static IReadOnlyDictionary<Type, (Type HandlerInterface, string Kind)> RegistrationMap { get; }");
            sb.AppendLine("            = new Dictionary<Type, (Type, string)>");
            sb.AppendLine("            {");
            foreach (var reg in registrations.Distinct())
            {
                sb.AppendLine($"                {{ typeof({reg.RequestTypeName}), (typeof({reg.HandlerInterfaceName}), \"{reg.RegistrationKind}\") }},");
            }
            sb.AppendLine("            };");

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Extract the HandlerKind from the attribute's constructor arguments.
        /// </summary>
        private static string ExtractHandlerKind(AttributeData handlerAttr)
        {
            //The attribute is [HandlerType(HandlerKind kind)] with 1 constructor argument
            
            if (handlerAttr.ConstructorArguments.Length != 1) return "Unknown";
            var enumVal = handlerAttr.ConstructorArguments[0].Value;
            if (enumVal != null)
                return enumVal.ToString() ?? "Unknown";
            return "Unknown";
        }

        // A record to hold discovered handler info
        private record struct RegistrationInfo(
            string RequestTypeName,
            string HandlerInterfaceName,
            string HandlerImplementationName,
            string RegistrationKind
        );

        /// <summary>
        /// Holds all the DiagnosticDescriptors we might emit.
        /// </summary>
        private static class Diagnostics
        {
            internal static readonly DiagnosticDescriptor MissingHandlerTypeAttribute = new DiagnosticDescriptor(
                id: "CQRHND001",
                title: "Missing HandlerTypeAttribute Symbol",
                messageFormat: "Could not find the HandlerTypeAttribute in the compilation. Make sure CQRSharp.Shared.Attributes is referenced.",
                category: "CQRSharp.Generators",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true
            );

            internal static readonly DiagnosticDescriptor HandlerInterfaceNoTypeArgs = new DiagnosticDescriptor(
                id: "CQRHND002",
                title: "Handler Interface Has No Generic Arguments",
                messageFormat: "Interface '{0}' is marked with [HandlerType], but has no generic arguments. Ignoring this interface.",
                category: "CQRSharp.Generators",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true
            );

            internal static readonly DiagnosticDescriptor NoHandlersFound = new DiagnosticDescriptor(
                id: "CQRHND003",
                title: "No Handlers Found",
                messageFormat: "No handler classes implementing a [HandlerType] interface were discovered.",
                category: "CQRSharp.Generators",
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true
            );

            internal static readonly DiagnosticDescriptor HandlerRegistrationSuccess = new DiagnosticDescriptor(
                id: "CQRHND004",
                title: "Handler Registration Succeeded",
                messageFormat: "Successfully generated handler registration for {0} discovered handler(s).",
                category: "CQRSharp.Generators",
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true
            );

            internal static readonly DiagnosticDescriptor HandlerRegistrationException = new DiagnosticDescriptor(
                id: "CQRHND999",
                title: "Handler Registration Generator Exception",
                messageFormat: "Unhandled exception in HandlerRegistrationGenerator: {0}",
                category: "CQRSharp.Generators",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true
            );
        }
    }
}
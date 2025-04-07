using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.Types
{
    /// <summary>
    /// A source generator that discovers classes implementing interfaces decorated
    /// with the <c>[HandlerType]</c> attribute and generates a registrar to register
    /// them with the DI container.
    /// </summary>
    [Generator]
    public class HandlerRegistrationGenerator : IIncrementalGenerator
    {
        #region Diagnostic Descriptors

        //When the HandlerType attribute cannot be found.
        private static readonly DiagnosticDescriptor SMissingAttributeDescriptor = new DiagnosticDescriptor(
            id: "CQRHND002",
            title: "Missing HandlerType Attribute",
            messageFormat: "The attribute 'CQRSharp.Shared.Attributes.Requests.HandlerTypeAttribute' was not found. Ensure that it is defined and referenced.",
            category: "CQRSharp.Generators",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        //When no handler registrations are discovered.
        private static readonly DiagnosticDescriptor SNoRegistrationFoundDescriptor = new DiagnosticDescriptor(
            id: "CQRHND003",
            title: "No Handler Registrations Found",
            messageFormat: "No handler registrations were discovered. Ensure that you have classes implementing interfaces decorated with [HandlerType].",
            category: "CQRSharp.Generators",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        //For unexpected exceptions during generation.
        private static readonly DiagnosticDescriptor SErrorDescriptor = new DiagnosticDescriptor(
            id: "CQRHND001",
            title: "Handler Registration Generator Exception",
            messageFormat: "An exception occurred in the HandlerRegistrationGenerator: {0}",
            category: "CQRSharp.Generators",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        //On successful generation.
        private static readonly DiagnosticDescriptor SSuccessDescriptor = new DiagnosticDescriptor(
            id: "CQRHND004",
            title: "Handler Registration Generation Successful",
            messageFormat: "Handler registration generation completed successfully. {0} registration(s) were generated.",
            category: "CQRSharp.Generators",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        //Debug diagnostics (set to Info during development; can be lowered later).
        private static readonly DiagnosticDescriptor SDebugStartDescriptor = new DiagnosticDescriptor(
            id: "CQRDBG001",
            title: "Debug: Generation Started",
            messageFormat: "Starting handler registration generation",
            category: "CQRSharp.Generators.Debug",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor SDebugCandidateCountDescriptor = new DiagnosticDescriptor(
            id: "CQRDBG002",
            title: "Debug: Candidate Count",
            messageFormat: "Found {0} candidate class declarations",
            category: "CQRSharp.Generators.Debug",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor SDebugProcessingClassDescriptor = new DiagnosticDescriptor(
            id: "CQRDBG003",
            title: "Debug: Processing Class",
            messageFormat: "Processing class: {0}",
            category: "CQRSharp.Generators.Debug",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor SDebugRegistrationAddedDescriptor = new DiagnosticDescriptor(
            id: "CQRDBG004",
            title: "Debug: Registration Added",
            messageFormat: "Added registration: Interface = {0}, Implementation = {1}",
            category: "CQRSharp.Generators.Debug",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        #endregion

        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //Process every class declaration in the compilation.
            IncrementalValuesProvider<ClassDeclarationSyntax> candidateClasses =
                context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node
                );

            //Combine all candidate classes with the current compilation.
            IncrementalValueProvider<(ImmutableArray<ClassDeclarationSyntax> Classes, Compilation Compilation)> compilationAndClasses =
                candidateClasses.Collect().Combine(context.CompilationProvider);

            //Register the output generation.
            context.RegisterSourceOutput(compilationAndClasses, (spc, source) =>
            {
                spc.ReportDiagnostic(Diagnostic.Create(SDebugStartDescriptor, Location.None));

                try
                {
                    var (classes, compilation) = source;
                    spc.ReportDiagnostic(Diagnostic.Create(SDebugCandidateCountDescriptor, Location.None, classes.Length));

                    //Look up the HandlerType attribute by its fully qualified metadata name.
                    var handlerTypeAttributeSymbol = compilation.GetTypeByMetadataName("CQRSharp.Shared.Attributes.Requests.HandlerTypeAttribute");
                    if (handlerTypeAttributeSymbol == null)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(SMissingAttributeDescriptor, Location.None));
                        return;
                    }

                    //Create a list to collect registrations:
                    //Each registration is a tuple (InterfaceFullName, ImplementationFullName).
                    var registrations = new List<(string InterfaceType, string ImplementationType)>();

                    //Loop over every class declaration in the compilation.
                    foreach (var classDecl in classes)
                    {
                        //Get the semantic model for this syntax tree.
                        var semanticModel = compilation.GetSemanticModel(classDecl.SyntaxTree);
                        if (semanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol)
                        {
                            continue;
                        }

                        spc.ReportDiagnostic(Diagnostic.Create(SDebugProcessingClassDescriptor, Location.None, classSymbol.ToDisplayString()));

                        //Check every interface that this class implements.
                        foreach (var iface in classSymbol.AllInterfaces)
                        {
                            //If the interface is decorated with [HandlerType], then register this class.
                            if (iface.GetAttributes().Any(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, handlerTypeAttributeSymbol)))
                            {
                                //Use fully qualified type names for safety with AOT and trimming.
                                string interfaceType = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                string implementationType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                registrations.Add((interfaceType, implementationType));
                                spc.ReportDiagnostic(Diagnostic.Create(SDebugRegistrationAddedDescriptor, Location.None, interfaceType, implementationType));
                                //Only register once per class.
                                break;
                            }
                        }
                    }

                    if (registrations.Count == 0)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(SNoRegistrationFoundDescriptor, Location.None));
                        return;
                    }

                    //Build the source code to be generated.
                    var sb = new StringBuilder();
                    sb.AppendLine("// <auto-generated />");
                    sb.AppendLine("using System;");
                    sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
                    sb.AppendLine("using CQRSharp.Core.SourceGeneration;");
                    sb.AppendLine("using System.Runtime.CompilerServices;");
                    sb.AppendLine();
                    sb.AppendLine("namespace CQRSharp.Generated");
                    sb.AppendLine("{");
                    sb.AppendLine("    /// <summary>");
                    sb.AppendLine("    /// A generated data registrar that registers discovered handler types with the DI container.");
                    sb.AppendLine("    /// </summary>");
                    sb.AppendLine("    public class GeneratedHandlerRegistrar : IDataRegistrar");
                    sb.AppendLine("    {");
                    sb.AppendLine("        /// <inheritdoc />");
                    sb.AppendLine("        public void RegisterData(IServiceCollection services)");
                    sb.AppendLine("        {");

                    //Emit one registration per discovered handler.
                    foreach (var reg in registrations.Distinct())
                    {
                        sb.AppendLine($"            services.AddTransient(typeof({reg.InterfaceType}), typeof({reg.ImplementationType}));");
                    }

                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    sb.AppendLine("    /// <summary>");
                    sb.AppendLine("    /// A module initializer that wires up the generated handler registrar with the global registrar.");
                    sb.AppendLine("    /// </summary>");
                    sb.AppendLine("    public static class GeneratedHandlerRegistrarInitializer");
                    sb.AppendLine("    {");
                    sb.AppendLine("        [ModuleInitializer]");
                    sb.AppendLine("        public static void Initialize()");
                    sb.AppendLine("        {");
                    sb.AppendLine("            try");
                    sb.AppendLine("            {");
                    sb.AppendLine("                //Wire the generated registrar into the global Registrar.");
                    sb.AppendLine("                Registrar.HandlerRegistrar = new GeneratedHandlerRegistrar();");
                    sb.AppendLine("            }");
                    sb.AppendLine("            catch(Exception ex)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                //Fail fast if the initialization does not succeed.");
                    sb.AppendLine("                throw new InvalidOperationException(\"Failed to initialize GeneratedHandlerRegistrar.\", ex);");
                    sb.AppendLine("            }");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                    sb.AppendLine("}");

                    //Add the generated source to the compilation.
                    spc.AddSource("GeneratedHandlerRegistrar.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
                    spc.ReportDiagnostic(Diagnostic.Create(SSuccessDescriptor, Location.None, registrations.Count));
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(SErrorDescriptor, Location.None, ex.ToString()));
                }
            });
        }
    }
}
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Shared.Attributes.Requests;
using CQRSharp.Shared.Core.Data.Models.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.Types;

/// <summary>
///     A source generator that builds a pipeline registry for requests, finding types that implement
///     interfaces decorated with [RequestMarker] (e.g., IQuery&lt;T&gt; or ICommand) and generating pipeline code.
/// </summary>
[Generator]
public sealed class PipelineRegistryGenerator : IIncrementalGenerator
{
    /// <summary>
    ///     Initializes the incremental source generator.
    /// </summary>
    /// <param name="context">The generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        //Identify candidate classes and records.
        var candidateTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol
            )
            .Where(symbol => symbol is not null);

        //Combine candidate types with the compilation.
        var compilationAndTypes = context.CompilationProvider.Combine(candidateTypes.Collect());

        //Register the source output.
        context.RegisterSourceOutput(compilationAndTypes, (spc, source) =>
        {
            var (compilation, types) = source;

            try
            {
                var pipelineEntries = ProcessTypes(spc, compilation, types);
                var sourceCode = GenerateRegistrarSource(pipelineEntries);

                spc.AddSource("GeneratedPipelineRegistrar.g.cs", SourceText.From(sourceCode, Encoding.UTF8));
                spc.ReportDiagnostic(Diagnostic.Create(
                    PipelineDiagnostics.PipelineBuilderSuccess,
                    Location.None,
                    pipelineEntries.Length
                ));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    PipelineDiagnostics.PipelineBuilderException,
                    Location.None,
                    ex.ToString()
                ));
            }
        });
    }

    /// <summary>
    ///     Examines the provided candidate types to see if they implement an interface with [RequestMarker].
    ///     If so, extracts relevant request/result type info.
    /// </summary>
    private static ImmutableArray<(string RequestType, string ResultType)> ProcessTypes(
        SourceProductionContext spc,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol?> types)
    {
        var pipelineEntriesBuilder = ImmutableArray.CreateBuilder<(string, string)>();

        //Retrieve the marker attribute symbol.
        var markerFullName = typeof(RequestMarkerAttribute).FullName;
        if (string.IsNullOrEmpty(markerFullName))
        {
            //If something is wrong with reflection on the attribute:
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.MissingRequestMarkerAttribute,
                Location.None
            ));
            return pipelineEntriesBuilder.ToImmutable();
        }

        var requestMarkerAttrSymbol = compilation.GetTypeByMetadataName(markerFullName);
        if (requestMarkerAttrSymbol is null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.MissingRequestMarkerAttribute,
                Location.None
            ));
            return pipelineEntriesBuilder.ToImmutable();
        }

        //Build the pipeline entries by checking candidate types.
        foreach (var typeSymbol in types)
        {
            var isRequest = typeSymbol != null && typeSymbol.AllInterfaces.Any(iface =>
                iface.GetAttributes().Any(a =>
                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, requestMarkerAttrSymbol)));

            if (!isRequest)
                continue;

            if (typeSymbol == null) continue;

            var requestFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var resultFullName = InferResultType(typeSymbol);

            pipelineEntriesBuilder.Add((requestFullName, resultFullName));
        }

        //If no requests found, emit a diagnostic.
        if (pipelineEntriesBuilder.Count == 0)
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.NoRequestsFound,
                Location.None
            ));

        return pipelineEntriesBuilder.ToImmutable();
    }

    /// <summary>
    ///     Infers the result type for a given request symbol:
    ///     - If the request implements IQuery&lt;T&gt;, returns T.
    ///     - If it implements ICommand, returns CommandResult.
    ///     - Otherwise, defaults to System.Object.
    /// </summary>
    private static string InferResultType(INamedTypeSymbol requestSymbol)
    {
        //Look for IQuery<T> implementation.
        var iQuery = requestSymbol.AllInterfaces
            .FirstOrDefault(i => i.Name == "IQuery" && i.TypeArguments.Length == 1);
        if (iQuery != null) return iQuery.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        //Check for ICommand implementation.
        var iCommand = requestSymbol.AllInterfaces
            .FirstOrDefault(i => i.Name == "ICommand");
        return iCommand != null ? typeof(CommandResult).FullName! : typeof(object).FullName!;
    }

    /// <summary>
    ///     Generates the registrar source code that registers the pipeline registry with the DI container.
    /// </summary>
    private static string GenerateRegistrarSource(ImmutableArray<(string RequestType, string ResultType)> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Concurrent;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using CQRSharp.Core.SourceGeneration;  //For IDataRegistrar and Registrar.");
        sb.AppendLine("using CQRSharp.Core.Pipelines;        //For IPipelineBehavior and related types.");
        sb.AppendLine("using CQRSharp.Core.Caching.Pipelines; //For IPipelineRegistry.");
        sb.AppendLine();
        sb.AppendLine("namespace CQRSharp.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(
            "    /// A generated registrar that registers an implementation of IPipelineRegistry with the DI container.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public sealed class GeneratedPipelineRegistrar : IDataRegistrar");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// A mapping of request types to their corresponding pipeline builder delegates.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        private readonly ConcurrentDictionary<Type, PipelineBuilderDelegate> _pipelineMap;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine(
            "        /// Initializes a new instance of the <see cref=\"GeneratedPipelineRegistrar\"/> class.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public GeneratedPipelineRegistrar()");
        sb.AppendLine("        {");
        sb.AppendLine("            _pipelineMap = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();");
        sb.AppendLine();

        var distinctEntries = entries.Distinct().ToArray();
        foreach (var (requestType, resultType) in distinctEntries)
        {
            sb.AppendLine(
                $"            _pipelineMap.TryAdd(typeof({requestType}), (services, requestObj, finalHandler, ct) =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                //Gather all pipeline behaviors for {requestType} -> {resultType}");
            sb.AppendLine(
                $"                var behaviors = services.GetServices(typeof(IPipelineBehavior<{requestType}, {resultType}>))");
            sb.AppendLine($"                    .Cast<IPipelineBehavior<{requestType}, {resultType}>>()");
            sb.AppendLine("                    .ToArray();");
            sb.AppendLine();
            sb.AppendLine(
                $"                Func<{requestType}, CancellationToken, Task<{resultType}>> pipeline = async (req, token) =>");
            sb.AppendLine("                {");
            sb.AppendLine("                    var rawResult = await finalHandler(token).ConfigureAwait(false);");
            sb.AppendLine($"                    return ({resultType}) rawResult;");
            sb.AppendLine("                };");
            sb.AppendLine();
            sb.AppendLine("                //Compose behaviors in reverse order so that each wraps the next.");
            sb.AppendLine("                for (int i = behaviors.Length - 1; i >= 0; i--)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var next = pipeline;");
            sb.AppendLine("                    var behavior = behaviors[i];");
            sb.AppendLine(
                "                    pipeline = (req, token) => behavior.Handle(req, x => next(req, x), token);");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("                return pipeline((requestObj as " + requestType +
                          ")!, ct).ContinueWith(t => (object)t.Result, ct);");
            sb.AppendLine("            });");
            sb.AppendLine();
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        /// <inheritdoc />");
        sb.AppendLine("        public void RegisterData(IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            //Register the pipeline map as a singleton IPipelineRegistry.");
        sb.AppendLine("            services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(_pipelineMap));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(
            "    /// A module initializer that assigns the generated pipeline registrar to the global Registrar.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static class GeneratedPipelineRegistrarInitializer");
        sb.AppendLine("    {");
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        public static void Initialize()");
        sb.AppendLine("        {");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                Registrar.PipelineRegistryRegistrar = new GeneratedPipelineRegistrar();");
        sb.AppendLine("            }");
        sb.AppendLine("            catch (Exception ex)");
        sb.AppendLine("            {");
        sb.AppendLine(
            "                throw new InvalidOperationException(\"Failed to initialize GeneratedPipelineRegistrar.\", ex);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    ///     Contains diagnostic descriptors used by the <see cref="PipelineRegistryGenerator" />.
    /// </summary>
    private static class PipelineDiagnostics
    {
        /// <summary>
        ///     Emitted when the RequestMarkerAttribute symbol is missing from the compilation.
        /// </summary>
        public static readonly DiagnosticDescriptor MissingRequestMarkerAttribute = new(
            "CQRPIP001",
            "Missing RequestMarkerAttribute Symbol",
            "Could not find the RequestMarkerAttribute in the compilation. Make sure CQRSharp.Shared.Attributes is referenced.",
            "CQRSharp.Generators",
            DiagnosticSeverity.Warning,
            true
        );

        /// <summary>
        ///     Emitted when no requests are found that implement a RequestMarker interface.
        /// </summary>
        public static readonly DiagnosticDescriptor NoRequestsFound = new(
            "CQRPIP002",
            "No Requests Found",
            "No classes/records implementing a [RequestMarker] interface were discovered",
            "CQRSharp.Generators",
            DiagnosticSeverity.Info,
            true
        );

        /// <summary>
        ///     Emitted when the generator successfully processes and creates pipeline builders.
        /// </summary>
        public static readonly DiagnosticDescriptor PipelineBuilderSuccess = new(
            "CQRPIP003",
            "Pipeline Builder Generator Succeeded",
            "Successfully generated pipeline builders for {0} discovered request(s)",
            "CQRSharp.Generators",
            DiagnosticSeverity.Info,
            true
        );

        /// <summary>
        ///     Emitted when any unhandled exception occurs within the PipelineRegistryGenerator.
        /// </summary>
        public static readonly DiagnosticDescriptor PipelineBuilderException = new(
            "CQRPIP999",
            "Pipeline Builder Generator Exception",
            "Unhandled exception in PipelineRegistryGenerator: {0}",
            "CQRSharp.Generators",
            DiagnosticSeverity.Error,
            true
        );
    }
}
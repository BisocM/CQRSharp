using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Shared.Data.Attributes.Pipelines;
using CQRSharp.Shared.Data.Attributes.Requests;
using CQRSharp.Shared.Data.Models.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.Types;

[Generator]
public sealed class PipelineRegistryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        //Identify candidate request types
        var requestCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol
            )
            .Where(symbol => symbol is not null);

        //Identify candidate pipeline behavior types
        var behaviorCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol
            )
            .Where(symbol => symbol is not null);

        //Combine compilation, requests, and behaviors
        var compilationAndCandidates = context.CompilationProvider
            .Combine(requestCandidates.Collect())
            .Combine(behaviorCandidates.Collect());

        context.RegisterSourceOutput(compilationAndCandidates, (spc, source) =>
        {
            var ((compilation, requestSymbols), behaviorSymbols) = source;
            var pipelineEntries = ProcessTypes(spc, compilation, requestSymbols, behaviorSymbols);
            var sourceCode = GenerateRegistrarSource(pipelineEntries);

            spc.AddSource("GeneratedPipelineRegistrar.g.cs", SourceText.From(sourceCode, Encoding.UTF8));
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.PipelineBuilderSuccess,
                Location.None,
                pipelineEntries.Length
            ));
        });
    }

    private static ImmutableArray<(string RequestType, string ResultType, ImmutableArray<string> Behaviors)>
        ProcessTypes(
            SourceProductionContext spc,
            Compilation compilation,
            ImmutableArray<INamedTypeSymbol?> requestSymbols,
            ImmutableArray<INamedTypeSymbol?> behaviorSymbols)
    {
        var entriesBuilder = ImmutableArray.CreateBuilder<(string, string, ImmutableArray<string>)>();

        //Retrieve the marker attribute symbol for requests
        var markerFullName = typeof(RequestMarkerAttribute).FullName;
        if (string.IsNullOrEmpty(markerFullName))
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.MissingRequestMarkerAttribute,
                Location.None
            ));
            return entriesBuilder.ToImmutable();
        }

        var requestMarkerAttrSymbol = compilation.GetTypeByMetadataName(markerFullName);
        if (requestMarkerAttrSymbol is null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.MissingRequestMarkerAttribute,
                Location.None
            ));
            return entriesBuilder.ToImmutable();
        }

        //Retrieve the IPipelineBehavior<> symbol
        var pipelineBehaviorSymbol = compilation.GetTypeByMetadataName("CQRSharp.Core.Pipelines.IPipelineBehavior`2");
        if (pipelineBehaviorSymbol is null)
            //No pipeline behavior interface found
            return entriesBuilder.ToImmutable();

        //Retrieve PipelinePriorityAttribute symbol
        var priorityAttrSymbol = compilation.GetTypeByMetadataName(typeof(PipelinePriorityAttribute).FullName!);
        //Default priority if attribute is absent
        var defaultPriority = PipelinePriorityAttribute.DefaultPriority;

        foreach (var requestSymbol in requestSymbols)
        {
            if (requestSymbol is null)
                continue;

            //Check if type implements a [RequestMarker] interface
            var isRequest = requestSymbol.AllInterfaces.Any(iface =>
                iface.GetAttributes().Any(a =>
                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, requestMarkerAttrSymbol)));
            if (!isRequest)
                continue;

            //Infer result type symbol
            var resultTypeSymbol = InferResultTypeSymbol(compilation, requestSymbol);

            var requestFullName = requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var resultFullName = resultTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            //Find matching pipeline behaviors
            var matchedBehaviors = new List<(INamedTypeSymbol Symbol, int Priority)>();
            foreach (var behaviorSymbol in behaviorSymbols)
            {
                if (behaviorSymbol is null)
                    continue;

                //Check if this behavior implements IPipelineBehavior<requestSymbol, resultTypeSymbol>
                var iface = behaviorSymbol.AllInterfaces.FirstOrDefault(i =>
                    SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, pipelineBehaviorSymbol)
                    && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], requestSymbol)
                    && SymbolEqualityComparer.Default.Equals(i.TypeArguments[1], resultTypeSymbol));
                if (iface is null)
                    continue;

                //Read priority from attribute, if present
                var priority = defaultPriority;
                var attrData = behaviorSymbol.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, priorityAttrSymbol));
                if (attrData != null && attrData.ConstructorArguments.Length == 1
                                     && attrData.ConstructorArguments[0].Value is int p)
                    priority = p;

                matchedBehaviors.Add((behaviorSymbol, priority));
            }

            //Sort behaviors by ascending priority (lower number = higher priority)
            var sortedBehaviorFullNames = matchedBehaviors
                .OrderBy(x => x.Priority)
                .Select(x => x.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .ToImmutableArray();

            entriesBuilder.Add((requestFullName, resultFullName, sortedBehaviorFullNames));
        }

        if (entriesBuilder.Count == 0)
            spc.ReportDiagnostic(Diagnostic.Create(
                PipelineDiagnostics.NoRequestsFound,
                Location.None
            ));

        return entriesBuilder.ToImmutable();
    }

    private static ITypeSymbol InferResultTypeSymbol(Compilation compilation, INamedTypeSymbol requestSymbol)
    {
        //Look for IQuery<T>
        var iQuery = requestSymbol.AllInterfaces
            .FirstOrDefault(i => i.Name == "IQuery" && i.TypeArguments.Length == 1);
        if (iQuery != null)
            return iQuery.TypeArguments[0];

        //Check for ICommand
        var iCommand = requestSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "ICommand");
        if (iCommand != null)
        {
            var commandResultSymbol = compilation.GetTypeByMetadataName(typeof(CommandResult).FullName!);
            if (commandResultSymbol != null)
                return commandResultSymbol;
        }

        //Fallback to object
        return compilation.GetSpecialType(SpecialType.System_Object);
    }

    private static string GenerateRegistrarSource(
        ImmutableArray<(string RequestType, string ResultType, ImmutableArray<string> Behaviors)> entries)
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
        sb.AppendLine("        private readonly ConcurrentDictionary<Type, PipelineBuilderDelegate> _pipelineMap;");
        sb.AppendLine();
        sb.AppendLine("        public GeneratedPipelineRegistrar()");
        sb.AppendLine("        {");
        sb.AppendLine("            _pipelineMap = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();");
        sb.AppendLine();

        var distinctEntries = entries.Distinct().ToArray();
        foreach (var (requestType, resultType, behaviors) in distinctEntries)
        {
            sb.AppendLine(
                $"            _pipelineMap.TryAdd(typeof({requestType}), (services, requestObj, finalHandler, ct) =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                //Compose pipeline behaviors for {requestType} -> {resultType}");

            if (behaviors.Length > 0)
            {
                sb.AppendLine($"                var behaviors = new IPipelineBehavior<{requestType}, {resultType}>[]");
                sb.AppendLine("                {");
                foreach (var behavior in behaviors)
                    sb.AppendLine($"                    services.GetRequiredService<{behavior}>(),");
                sb.AppendLine("                };");
            }
            else
            {
                sb.AppendLine(
                    $"                var behaviors = Array.Empty<IPipelineBehavior<{requestType}, {resultType}>>();");
            }

            sb.AppendLine();
            sb.AppendLine("                //Final handler delegate");
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
        sb.AppendLine("        public void RegisterData(IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(_pipelineMap));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
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

    private static class PipelineDiagnostics
    {
        public static readonly DiagnosticDescriptor MissingRequestMarkerAttribute = new(
            "CQRPIP001",
            "Missing RequestMarkerAttribute Symbol",
            "Could not find the RequestMarkerAttribute in the compilation. Make sure CQRSharp.Shared.Attributes is referenced.",
            "CQRSharp.Generators",
            DiagnosticSeverity.Warning,
            true
        );

        public static readonly DiagnosticDescriptor NoRequestsFound = new(
            "CQRPIP002",
            "No Requests Found",
            "No classes/records implementing a [RequestMarker] interface were discovered",
            "CQRSharp.Generators",
            DiagnosticSeverity.Info,
            true
        );

        public static readonly DiagnosticDescriptor PipelineBuilderSuccess = new(
            "CQRPIP003",
            "Pipeline Builder Generator Succeeded",
            "Successfully generated pipeline builders for {0} discovered request(s)",
            "CQRSharp.Generators",
            DiagnosticSeverity.Info,
            true
        );
    }
}
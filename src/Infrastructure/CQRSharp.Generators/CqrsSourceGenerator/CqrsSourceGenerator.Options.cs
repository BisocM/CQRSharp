using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private readonly record struct GeneratorConfig
    {
        public GeneratorConfig(bool suppressMissingRequestHandlerDiagnostics, bool isCompositionRoot)
        {
            SuppressMissingRequestHandlerDiagnostics = suppressMissingRequestHandlerDiagnostics;
            IsCompositionRoot = isCompositionRoot;
        }

        public bool SuppressMissingRequestHandlerDiagnostics { get; }

        /// <summary>
        ///     Whether this assembly is the composition root that emits the global <c>AddCqrsGenerated</c>/<c>AddGenerated</c>
        ///     entry points and wires every referenced module. Explicit via the <c>CQRSharpCompositionRoot</c> property, else
        ///     defaulted to executable outputs (<c>OutputType=Exe</c>/<c>WinExe</c>).
        /// </summary>
        public bool IsCompositionRoot { get; }

        public static GeneratorConfig From(AnalyzerConfigOptions options)
        {
            var suppressMissingHandlers = TryGetBool(
                options,
                GeneratorConfigKeys.SuppressMissingRequestHandlerDiagnosticsBuildProperty,
                GeneratorConfigKeys.SuppressMissingRequestHandlerDiagnosticsEditorConfig);

            return new GeneratorConfig(suppressMissingHandlers, ResolveCompositionRoot(options));
        }

        private static bool ResolveCompositionRoot(AnalyzerConfigOptions options)
        {
            if (options.TryGetValue(GeneratorConfigKeys.CompositionRootBuildProperty, out var raw) &&
                !string.IsNullOrWhiteSpace(raw))
            {
                if (bool.TryParse(raw, out var parsed)) return parsed;
                if (raw is "1") return true;
                if (raw is "0") return false;
            }

            // Default: an executable is the composition root. Libraries opt in via CQRSharpCompositionRoot=true.
            return options.TryGetValue(GeneratorConfigKeys.OutputTypeBuildProperty, out var outputType) &&
                   (string.Equals(outputType, "Exe", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outputType, "WinExe", System.StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetBool(
            AnalyzerConfigOptions options,
            string buildPropertyKey,
            string editorConfigKey)
        {
            if (options.TryGetValue(buildPropertyKey, out var raw) ||
                options.TryGetValue(editorConfigKey, out raw))
            {
                if (bool.TryParse(raw, out var parsed))
                    return parsed;

                if (raw is "1")
                    return true;
            }

            return false;
        }
    }

    private static class GeneratorConfigKeys
    {
        public const string SuppressMissingRequestHandlerDiagnosticsBuildProperty =
            "build_property.CQRSharpSuppressMissingRequestHandlerDiagnostics";

        public const string SuppressMissingRequestHandlerDiagnosticsEditorConfig =
            "cqrsharp_generator.suppress_missing_request_handler_diagnostics";

        public const string CompositionRootBuildProperty = "build_property.CQRSharpCompositionRoot";

        public const string OutputTypeBuildProperty = "build_property.OutputType";
    }
}
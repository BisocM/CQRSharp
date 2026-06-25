using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private readonly struct GeneratorConfig
    {
        public GeneratorConfig(bool suppressMissingRequestHandlerDiagnostics)
        {
            SuppressMissingRequestHandlerDiagnostics = suppressMissingRequestHandlerDiagnostics;
        }

        public bool SuppressMissingRequestHandlerDiagnostics { get; }

        public static GeneratorConfig From(AnalyzerConfigOptions options)
        {
            var suppressMissingHandlers = TryGetBool(
                options,
                GeneratorConfigKeys.SuppressMissingRequestHandlerDiagnosticsBuildProperty,
                GeneratorConfigKeys.SuppressMissingRequestHandlerDiagnosticsEditorConfig);

            return new GeneratorConfig(suppressMissingHandlers);
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
    }
}

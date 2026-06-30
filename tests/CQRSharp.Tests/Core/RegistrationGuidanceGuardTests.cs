using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Locks in the single correct registration verb across CQRSharp.Core's user-facing strings. Two near-miss verbs
///     used to leak into exception messages and lead developers astray: <c>AddCqrs()</c> registers the dispatcher but
///     not the generated routing (now hidden via <see cref="System.ComponentModel.EditorBrowsableAttribute" /> and
///     flagged by CQRA014), and <c>AddGenerated()</c> is the internal bootstrap a consumer cannot call. Every message
///     must point at <c>AddCqrsGenerated(...)</c>. This source-level guard keeps any future throw from regressing.
/// </summary>
public class RegistrationGuidanceGuardTests
{
    [Theory]
    [InlineData("services.AddGenerated()")]
    [InlineData("AddGenerated()")]
    [InlineData("services.AddCqrs()")]
    public void CoreSource_DoesNotInstructCallersToUseTheWrongRegistrationVerb(string forbidden)
    {
        var coreDir = LocateCoreSourceDir();
        if (coreDir is null) return; // not running from the repo tree (e.g. a packaged build); nothing to scan

        var offenders = Directory
            .EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty(
            $"CQRSharp.Core must never instruct a consumer to call '{forbidden}' — use AddCqrsGenerated(...) instead " +
            $"(offending files: {string.Join(", ", offenders)})");
    }

    // Resolve CQRSharp.Core's source by walking up from the test binary's runtime location. A real filesystem path is
    // used deliberately rather than a [CallerFilePath] value, which deterministic CI builds (ContinuousIntegrationBuild)
    // normalize to a synthetic root such as "/_/". Returns null when the repo tree is not reachable, so the scan is
    // skipped rather than failed.
    private static string? LocateCoreSourceDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Domain", "CQRSharp.Core");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }
}

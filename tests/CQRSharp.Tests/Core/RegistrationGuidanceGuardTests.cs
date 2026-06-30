using System.Runtime.CompilerServices;
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

        var offenders = Directory
            .EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty(
            $"CQRSharp.Core must never instruct a consumer to call '{forbidden}' — use AddCqrsGenerated(...) instead " +
            $"(offending files: {string.Join(", ", offenders)})");
    }

    // The repo layout is fixed relative to this test's own source file, so resolve it from the compile-time path rather
    // than guessing from the test binary's runtime location (which varies by TFM/output dir).
    private static string LocateCoreSourceDir([CallerFilePath] string testFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(testFilePath)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Domain", "CQRSharp.Core")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repo root containing src/Domain/CQRSharp.Core must be reachable from the test source");
        return Path.Combine(dir!.FullName, "src", "Domain", "CQRSharp.Core");
    }
}

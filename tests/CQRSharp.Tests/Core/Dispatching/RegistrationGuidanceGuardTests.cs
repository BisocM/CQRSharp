using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Keeps every package's user-facing text pointing at the one registration verb, <c>AddCqrsGenerated(...)</c>:
///     <c>services.AddCqrs()</c> registers the dispatcher without the generated routing, and <c>AddGenerated()</c> does not
///     exist (the generated registrations are private to the bootstrap). A message or doc comment naming either as the
///     thing to call sends the reader to a setup whose first Send throws, or that does not compile. The scan reads the
///     source tree, since the strings live in exception messages, diagnostics and doc comments that no behavior test
///     enumerates.
/// </summary>
public sealed class RegistrationGuidanceGuardTests
{
    [Theory]
    [InlineData("AddGenerated()")]
    [InlineData("services.AddCqrs()")]
    public void No_package_names_the_wrong_registration_verb(string forbidden)
    {
        var root = RepositoryRoot();
        var src = Path.Combine(root, "src");
        var sources = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(Path.GetRelativePath(src, file)))
            .ToList();
        sources.Should().NotBeEmpty("a scan that reads nothing proves nothing");

        var offenders = sources
            .Where(file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        offenders.Should().BeEmpty($"no package may tell a consumer to call '{forbidden}'; AddCqrsGenerated(...) is the verb");
    }

    // Walks up from the test binary rather than using [CallerFilePath]: deterministic CI builds map source paths to a
    // synthetic root ("/_/"). This project only runs from a checkout, so not finding it is a failure, not a skip.
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "CQRSharp.Core", "CQRSharp.Core.csproj")))
                return dir.FullName;

        throw new DirectoryNotFoundException($"No repository containing src/CQRSharp.Core was found above '{AppContext.BaseDirectory}'.");
    }

    private static bool IsBuildOutput(string pathUnderSrc)
    {
        var segments = pathUnderSrc.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }
}

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Shared;

/// <summary>Metadata references for the compilations the generator and analyzer tests build in memory.</summary>
internal static class ProbeReferences
{
    private static readonly string[] TrustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => !string.IsNullOrEmpty(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // One reference per file for the whole run: Roslyn then reuses each assembly's metadata and symbols across probe
    // compilations instead of re-reading some 450 images for every one.
    private static readonly ConcurrentDictionary<string, MetadataReference> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Every assembly the test host can resolve — the shared framework and each dependency in the deps file, CQRSharp's
    ///     included. Built from that fixed list rather than from the assemblies loaded so far, so a probe compiles against
    ///     the same references whichever tests ran before it.
    /// </summary>
    /// <param name="exclude">Drops a reference by file name, for a probe that must not see an assembly.</param>
    public static MetadataReference[] Create(Func<string, bool>? exclude = null)
        => TrustedPlatformAssemblies
            .Where(path => exclude is null || !exclude(Path.GetFileName(path)))
            .Select(path => Cache.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p)))
            .ToArray();

    /// <summary>
    ///     The references of an application made of one project: every assembly the test host can resolve but the test
    ///     assemblies themselves, which are CQRSharp modules an application would have to compose.
    /// </summary>
    public static MetadataReference[] SingleProjectApplication()
        => Create(name => name.StartsWith("CQRSharp.Tests", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     The references of a project that uses CQRSharp.Abstractions and nothing else of CQRSharp: a contracts or handler
    ///     library that does not reference the runtime.
    /// </summary>
    public static MetadataReference[] AbstractionsOnly()
        => Create(name => name.StartsWith("CQRSharp.", StringComparison.OrdinalIgnoreCase) &&
                          !name.Equals("CQRSharp.Abstractions.dll", StringComparison.OrdinalIgnoreCase));
}

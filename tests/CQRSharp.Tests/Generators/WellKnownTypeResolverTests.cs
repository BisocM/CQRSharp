using CQRSharp.Core.Mediation;
using CQRSharp.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PublicRole = CQRSharp.Abstractions.Attributes.SourceGeneration.CqrsRole;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Drift guard for the well-known-type handoff. CQRSharp.Core declares every framework anchor via <c>typeof(...)</c>;
///     the shared <see cref="CqrsKnownSymbols" /> resolver reads them back from referenced metadata. These tests compile
///     against the real Core + Abstractions assemblies and assert the round-trip is complete and correctly normalized,
///     so a rename, a missed Core declaration, or an un-normalized generic symbol all fail the build.
/// </summary>
public sealed class WellKnownTypeResolverTests
{
    private static MetadataReference[] CoreAndAbstractionsReferences()
    {
        var paths = new HashSet<string>(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase)
        {
            typeof(ICqrsDispatcher).Assembly.Location, // CQRSharp.Core — carries the [assembly: CqrsWellKnownType] declarations
            typeof(PublicRole).Assembly.Location       // CQRSharp.Abstractions — defines the attribute + enum
        };

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }

    private static MetadataReference[] AbstractionsOnlyReferences()
    {
        // The test host's TPA set already contains CQRSharp.Core.dll (the test references Core), so simulating an
        // Abstractions-only consumer means explicitly excluding Core — the only assembly that declares the attributes.
        var paths = new HashSet<string>(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p))
            .Where(p => !Path.GetFileName(p).Equals("CQRSharp.Core.dll", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase)
        {
            typeof(PublicRole).Assembly.Location // Abstractions, but NOT Core
        };

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }

    private static Compilation Compile(string source, MetadataReference[] references)
        => CSharpCompilation.Create(
            "WellKnownTypeResolverUnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact(DisplayName = "Every CqrsRole resolves to a framework symbol against real Core + Abstractions metadata")]
    public void EveryRole_Resolves()
    {
        var known = CqrsKnownSymbols.For(Compile("// empty consumer", CoreAndAbstractionsReferences()));

        known.AbstractionsPresent.Should().BeTrue();
        known.CoreReferenced.Should().BeTrue();
        known.GetMissingRequiredRoleNames().Should().BeEmpty();

        foreach (CqrsRole role in Enum.GetValues(typeof(CqrsRole)))
            known.Get(role).Should().NotBeNull(
                $"role '{role}' must be declared via [assembly: CqrsWellKnownType] in CQRSharp.Core");
    }

    [Fact(DisplayName = "Generic roles are stored as their OriginalDefinition (unbound typeof normalized)")]
    public void GenericRoles_AreOriginalDefinitions()
    {
        var known = CqrsKnownSymbols.For(Compile("// empty consumer", CoreAndAbstractionsReferences()));

        // The single most important invariant: every stored generic symbol is the OriginalDefinition, not the unbound
        // typeof, so downstream SymbolEqualityComparer checks against iface.OriginalDefinition match.
        foreach (CqrsRole role in Enum.GetValues(typeof(CqrsRole)))
        {
            var symbol = known.Get(role);
            if (symbol is not { IsGenericType: true }) continue;

            symbol.IsUnboundGenericType.Should().BeFalse($"role '{role}' must be stored as its OriginalDefinition");
            symbol.Should().BeSameAs(symbol.OriginalDefinition, $"role '{role}' must already be the OriginalDefinition");
        }
    }

    [Theory(DisplayName = "Generic roles resolve to the expected arity")]
    [InlineData(nameof(CqrsRole.ICommandHandler2), 2)]
    [InlineData(nameof(CqrsRole.IQueryHandler3), 3)]
    [InlineData(nameof(CqrsRole.IStreamRequestHandler3), 3)]
    [InlineData(nameof(CqrsRole.INotificationHandler), 1)]
    [InlineData(nameof(CqrsRole.IQuery), 1)]
    [InlineData(nameof(CqrsRole.IPipelineBehavior), 2)]
    public void GenericRole_HasExpectedArity(string roleName, int expectedArity)
    {
        var role = (CqrsRole)Enum.Parse(typeof(CqrsRole), roleName);
        var symbol = CqrsKnownSymbols.For(Compile("// empty consumer", CoreAndAbstractionsReferences())).Get(role);

        symbol.Should().NotBeNull();
        symbol!.Arity.Should().Be(expectedArity);
    }

    [Fact(DisplayName = "Resolved handler symbol compare-equals a real implementation's interface OriginalDefinition")]
    public void Resolved_Matches_RealImplementationInterface()
    {
        // The end-to-end invariant the whole design rests on: the resolver's stored symbol must compare-equal (via
        // SymbolEqualityComparer) to the OriginalDefinition of the interface on a real handler implementation in a
        // consumer compilation — exactly what the generator and analyzers do.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using CQRSharp.Abstractions.Interfaces.Context;
            using CQRSharp.Abstractions.Interfaces.Handlers;
            using CQRSharp.Abstractions.Interfaces.Markers.Command;
            using CQRSharp.Abstractions.Models.Commands;
            using CQRSharp.Abstractions.Models.Requests;

            public sealed class C : ICommand
            {
                public IRequestContext? Context { get; set; }
                public RequestMetadata? Metadata { get; set; }
            }

            public sealed class H : ICommandHandler<C>
            {
                public Task<CommandResult> Handle(C command, CancellationToken cancellationToken)
                    => Task.FromResult(CommandResult.FromSuccess());
            }
            """;

        var compilation = Compile(source, CoreAndAbstractionsReferences());
        var known = CqrsKnownSymbols.For(compilation);

        var commandHandler1 = known.ICommandHandler1;
        commandHandler1.Should().NotBeNull();

        var handler = compilation.GetTypeByMetadataName("H");
        handler.Should().NotBeNull();

        var iface = handler!.AllInterfaces.Single(i => i is { Name: "ICommandHandler", Arity: 1 });
        SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, commandHandler1).Should().BeTrue(
            "an unbound typeof must round-trip so that the stored OriginalDefinition matches a real handler interface");
    }

    [Fact(DisplayName = "Shared CqrsRole mirrors the public CqrsRole exactly (completeness)")]
    public void SharedRole_Mirrors_PublicRole()
    {
        var shared = Enum.GetNames(typeof(CqrsRole)).OrderBy(n => n, StringComparer.Ordinal);
        var published = Enum.GetNames(typeof(PublicRole)).OrderBy(n => n, StringComparer.Ordinal);

        shared.Should().Equal(published,
            "the internal resolver enum and the public CqrsRole must stay in sync (they are matched by member name)");
    }

    [Fact(DisplayName = "An Abstractions-only consumer reports no missing roles (no per-role error spam)")]
    public void WithoutCore_DoesNotSpamErrors()
    {
        var known = CqrsKnownSymbols.For(Compile("// abstractions only", AbstractionsOnlyReferences()));

        known.AbstractionsPresent.Should().BeTrue();
        known.CoreReferenced.Should().BeFalse();
        known.GetMissingRequiredRoleNames().Should().BeEmpty(
            "an Abstractions-only consumer legitimately lacks CQRSharp.Core and must not be flooded with per-role errors");
    }
}

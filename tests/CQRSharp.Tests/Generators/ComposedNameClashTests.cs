using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     CQRGEN020 / CQRGEN021: a notification name or a notification handler name that the modules one assembly composes
///     give to more than one type across assemblies (the runtime's CQRCONF010 / CQRCONF009 once the outbox is on). The
///     composing assembly reads each referenced module's names from its metadata, and reports at its AddCqrsGenerated calls.
/// </summary>
public sealed class ComposedNameClashTests
{
    private const string Usings = """
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using CQRSharp;
                                  using Microsoft.Extensions.DependencyInjection;

                                  """;

    private const string Composition = """

                                       public static class Wiring
                                       {
                                           public static void Wire(IServiceCollection services) => services.AddCqrsGenerated();
                                       }
                                       """;

    [Fact(DisplayName = "CQRGEN020: this assembly's notification and a referenced module's share a name; this assembly's keeps it")]
    public void Notification_name_shared_with_a_referenced_module()
    {
        var library = Module("LibA", """
            namespace LibA;
            [NotificationName("orders.placed")] public sealed record Placed : INotification;
            """);

        var run = Compose("""
            namespace Host;
            [NotificationName("orders.placed")] public sealed record HostPlaced : INotification;
            """ + Composition, library);

        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN020").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Location.GetLineSpan().Path.Should().Be("Probe0.cs");
        diagnostic.GetMessage().Should().Contain("'orders.placed'").And.Contain("'LibA.Placed' in 'LibA'")
            .And.Contain("'Host.HostPlaced' in this assembly").And.Contain("so 'Host.HostPlaced' keeps the name").And.Contain("CQRCONF010");
    }

    [Fact(DisplayName = "CQRGEN020: two referenced modules share a name; the one registered last keeps it")]
    public void Notification_name_shared_by_two_referenced_modules()
    {
        var libA = Module("LibA", "namespace LibA; [NotificationName(\"orders.placed\")] public sealed record Placed : INotification;");
        var libB = Module("LibB", "namespace LibB; [NotificationName(\"orders.placed\")] public sealed record Placed : INotification;");

        var run = Compose("namespace Host;" + Composition, libA, libB);

        run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN020")
            .Which.GetMessage().Should().Contain("so 'LibB.Placed' keeps the name");
    }

    [Fact(DisplayName = "CQRGEN020: nothing is reported for distinct names, for an assembly that composes nothing, or for a name that is no stable name")]
    public void Notification_names_without_a_clash()
    {
        var library = Module("LibA", """
            namespace LibA;
            [NotificationName("orders.placed")] public sealed record Placed : INotification;
            [NotificationName("   ")] public sealed record Blank : INotification;
            """);

        Compose("namespace Host; [NotificationName(\"orders.shipped\")] public sealed record Shipped : INotification;" + Composition, library)
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN020");
        Compose("namespace Host; [NotificationName(\"orders.placed\")] public sealed record HostPlaced : INotification;", library)
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN020", "an assembly that never calls AddCqrsGenerated composes nothing");
        Compose("namespace Host; public sealed record Blank : INotification;" + Composition, library)
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN020");
    }

    [Fact(DisplayName = "CQRGEN021: two assemblies declare handlers with one default name (the same namespace and type name)")]
    public void Handler_name_shared_across_assemblies()
    {
        static string Handler(string notification) => $$"""
            namespace Shared.Handlers;
            public sealed record {{notification}} : INotification;
            public sealed class PingHandler : INotificationHandler<{{notification}}>
            {
                public Task Handle({{notification}} notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;
        var library = Module("LibA", Handler("Ping"));

        var run = Compose(Handler("HostPing") + Composition, library);

        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN021").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("'Shared.Handlers.PingHandler'").And.Contain("in 'LibA'").And.Contain("in this assembly").And.Contain("CQRCONF009");
    }

    [Fact(DisplayName = "CQRGEN021: one [NotificationHandlerName] in two assemblies is reported; distinct names, and a handler the module does not subscribe, are not")]
    public void Explicit_handler_names()
    {
        static string Handler(string ns, string name, string accessibility = "public") => $$"""
            namespace {{ns}};
            public sealed record Ping : INotification;
            public static class Outer
            {
                [NotificationHandlerName("{{name}}")]
                {{accessibility}} sealed class PingHandler : INotificationHandler<Ping>
                {
                    public Task Handle(Ping notification, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        var library = Module("LibA", Handler("LibA", "orders.audit"));
        var privateHandler = Module("LibB", Handler("LibB", "orders.audit", "private"));

        Compose(Handler("Host", "orders.audit") + Composition, library)
            .GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN021").Which.GetMessage().Should().Contain("'orders.audit'");
        Compose(Handler("Host", "orders.host-audit") + Composition, library)
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN021");
        Compose(Handler("Host", "orders.audit") + Composition, privateHandler)
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN021", "generated code cannot name a private handler, so it has no subscription");
    }

    private static MetadataReference Module(string name, string source)
    {
        var run = CompilationHarness.RunGenerators([Usings + source], assemblyName: name);
        run.CompileErrors.Should().BeEmpty();
        return run.Reference!;
    }

    private static GeneratorRun Compose(string source, params MetadataReference[] modules)
    {
        var run = CompilationHarness.RunGenerators([Usings + source], modules, assemblyName: "Host");
        run.CompileErrors.Should().BeEmpty();
        return run;
    }
}

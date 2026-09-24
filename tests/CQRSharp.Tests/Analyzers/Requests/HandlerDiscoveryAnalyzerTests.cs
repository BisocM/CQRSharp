using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>CQRA003 (a dispatched request with no handler) and CQRA006 (a published notification with no subscriber).</summary>
public sealed class HandlerDiscoveryAnalyzerTests
{
    private const string Usings = """
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using CQRSharp;

                                  """;

    [Fact(DisplayName = "CQRA003: Send of a request with no handler is flagged")]
    public async Task Unhandled_request_is_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Unhandled : CommandBase;
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Send(new Unhandled());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA003" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "CQRA003: Send of a request with a handler is not flagged")]
    public async Task Handled_request_is_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Handled : CommandBase;
            public sealed class HandledHandler : ICommandHandler<Handled>
            {
                public Task<CommandResult> Handle(Handled command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
            }
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Send(new Handled());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003: a value-returning command handled by an IResultCommandHandler is not flagged")]
    public async Task Result_command_handler_counts()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Mint : ResultCommandBase<string>;
            public sealed class MintHandler : IResultCommandHandler<Mint, string>
            {
                public Task<CommandResult<string>> Handle(Mint command, CancellationToken cancellationToken) => Task.FromResult(CommandResult<string>.FromSuccess("t"));
            }
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Send(new Mint());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003: an abstract handler base with no concrete subclass handles nothing")]
    public async Task Abstract_handler_base_does_not_count()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Ping : CommandBase;
            public abstract class PingHandlerBase : ICommandHandler<Ping>
            {
                public abstract Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken);
            }
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Send(new Ping());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003 matches a request generic over a tuple whatever its element names")]
    public async Task Tuple_element_names_do_not_hide_a_handler()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Pair<T> : QueryBase<int>;
            public sealed class PairHandler : IQueryHandler<Pair<(int A, int B)>, int>
            {
                public Task<int> Handle(Pair<(int A, int B)> query, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            public static class Use
            {
                public static Task<int> Run(ICqrsDispatcher d) => d.Send(new Pair<(int X, int Y)>());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003: a handled request nested in a generic type is analyzed without crashing")]
    public async Task Request_nested_in_a_generic_type_with_a_handler()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public class Outer<T>
            {
                public sealed class Ping : CommandBase;
            }
            public sealed class PingHandler : ICommandHandler<Outer<int>.Ping>
            {
                public Task<CommandResult> Handle(Outer<int>.Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
            }
            public static class Use
            {
                public static Task Run(ICqrsDispatcher d) => d.Send(new Outer<int>.Ping());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003: an unhandled request nested in a generic type is flagged")]
    public async Task Request_nested_in_a_generic_type_without_a_handler()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public class Outer<T>
            {
                public sealed class Ping : CommandBase;
            }
            public static class Use
            {
                public static Task Run(ICqrsDispatcher d) => d.Send(new Outer<int>.Ping());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA003");
    }

    [Fact(DisplayName = "CQRA003: a marker for a request nested in a generic type does not turn the check off for the compilation")]
    public async Task Own_marker_for_a_nested_generic_request_is_read()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using System.Threading.Tasks;
            using CQRSharp;

            [assembly: CQRSharp.Core.SourceGeneration.CqrsHandledRequest(typeof(Outer<int>.Ping))]

            public class Outer<T>
            {
                public sealed class Ping : CommandBase;
            }
            public sealed class Unhandled : CommandBase;
            public static class Use
            {
                public static Task Run(ICqrsDispatcher d) => Task.WhenAll(d.Send(new Outer<int>.Ping()), d.Send(new Unhandled()));
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA003").Which.GetMessage().Should().Contain("Unhandled");
    }

    [Fact(DisplayName = "CQRA003: a referenced assembly's marker for a request nested in a generic type counts as its handler")]
    public async Task Referenced_marker_for_a_nested_generic_request_is_read()
    {
        var library = CompilationHarness.RunGenerators([Usings + """
            public class Outer<T>
            {
                public sealed class Ping : CommandBase;
            }
            public sealed class PingHandler : ICommandHandler<Outer<int>.Ping>
            {
                public Task<CommandResult> Handle(Outer<int>.Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
            }
            """], assemblyName: "NestedGenericLibrary");
        library.CompileErrors.Should().BeEmpty();
        library.Generated("CqrsGeneratedAssemblyMarkers.g.cs").Should().Contain("typeof(global::Outer<int>.Ping)");

        var consumer = CompilationHarness.Compile([Usings + """
            public sealed class Unhandled : CommandBase;
            public static class Use
            {
                public static Task Run(ICqrsDispatcher d) => Task.WhenAll(d.Send(new Outer<int>.Ping()), d.Send(new Unhandled()));
            }
            """], references: ProbeReferences.Create().Append(library.Reference!));

        var diagnostics = await CompilationHarness.AnalyzeAsync(consumer, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA003").Which.GetMessage().Should().Contain("Unhandled");
    }

    [Fact(DisplayName = "CQRA006: Publish of a notification with no subscriber is flagged")]
    public async Task Unheard_notification_is_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Unheard : INotification;
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Publish(new Unheard());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA006" && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact(DisplayName = "CQRA006: a notification with a handler is not flagged")]
    public async Task Heard_notification_is_not_flagged()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public sealed class Heard : INotification;
            public sealed class HeardHandler : INotificationHandler<Heard>
            {
                public Task Handle(Heard notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Publish(new Heard());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA006");
    }

    [Fact(DisplayName = "CQRA006: a notification handled through its base class or an interface is not flagged")]
    public async Task Base_type_and_interface_subscribers_count()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(Usings + """
            public interface IAudited : INotification;
            public abstract class DomainEvent : INotification;
            public sealed class OrderPlaced : DomainEvent;
            public sealed class UserCreated : IAudited;
            public sealed class OnDomainEvent : INotificationHandler<DomainEvent>
            {
                public Task Handle(DomainEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class OnAudited : INotificationHandler<IAudited>
            {
                public Task Handle(IAudited notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => Task.WhenAll(d.Publish(new OrderPlaced()), d.Publish(new UserCreated()));
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA006");
    }

    [Fact(DisplayName = "CQRA006: a notification handled in generated code (its marker is in this compilation) is not flagged")]
    public async Task Own_notification_marker_counts()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync("""
            using System.Threading.Tasks;
            using CQRSharp;

            [assembly: CQRSharp.Core.SourceGeneration.CqrsHandledNotification(typeof(Generated))]

            public sealed class Generated : INotification;
            public static class Consumer
            {
                public static Task Run(ICqrsDispatcher d) => d.Publish(new Generated());
            }
            """, new HandlerDiscoveryAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA006");
    }
}

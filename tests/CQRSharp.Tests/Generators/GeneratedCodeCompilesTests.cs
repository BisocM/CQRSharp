using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using AotGen = CQRSharp.Generators.CqrsAotHintGenerator.CqrsAotHintGenerator;
using Gen = CQRSharp.Generators.CqrsSourceGenerator.CqrsSourceGenerator;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Runs both generators over legal user code and then compiles what they emitted: the generator must never turn a
///     valid project into a broken build, nor silently drop a binding. Each case pins a shape that used to do one of
///     those (5.0.0).
/// </summary>
public sealed class GeneratedCodeCompilesTests
{
    private const string Usings = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Pipelines;

namespace ProbeNs;
";

    [Fact(DisplayName = "A partial handler declared across two files is one handler, not 'multiple handlers' (CQRGEN004)")]
    public void Partial_handler_is_a_single_candidate()
    {
        var run = Run(
            Usings + @"
public sealed class Ping : CommandBase;
public sealed partial class PingHandler : ICommandHandler<Ping>
{
    public Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}",
            Usings + "public sealed partial class PingHandler { private int _unused; }");

        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN004");
        run.CompileErrors.Should().BeEmpty();
    }

    [Fact(DisplayName = "An array result type is bound, not dropped as 'inaccessible' (CQRGEN010)")]
    public void Array_result_is_bound()
    {
        var run = Run(Usings + @"
public sealed record UserDto(string Name);
public sealed class GetUsers : QueryBase<UserDto[]>;
public sealed class GetUsersHandler : IQueryHandler<GetUsers, UserDto[]>
{
    public Task<UserDto[]> Handle(GetUsers query, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<UserDto>());
}");

        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN010" || d.Id == "CQRGEN003");
        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain("GetUsersHandler");
    }

    [Fact(DisplayName = "Interceptor attribute arguments are rebuilt faithfully: named, params/array, escaped and non-int literals")]
    public void Attribute_arguments_round_trip()
    {
        var run = Run(Usings + @"
public sealed class AuditAttribute : Attribute, IPostHandlerAttribute
{
    public AuditAttribute(string path, long weight, char flag, params string[] roles) { }
    public string Category { get; set; } = """";
    public int PostHandlerExecutionPriority => 0;
    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}

[Audit(@""C:\temp\new"", -5L, 'x', ""admin"", ""ops"", Category = ""billing"")]
public sealed class Charge : CommandBase;
public sealed class ChargeHandler : ICommandHandler<Charge>
{
    public Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain(@"""C:\\temp\\new""", "the backslashes must be escaped, not emitted raw");
        module.Should().Contain("(long)(-5)");
        module.Should().Contain("'x'");
        module.Should().Contain(@"new string[] { ""admin"", ""ops"" }", "a params argument must not collapse to null");
        module.Should().Contain(@"Category = ""billing""", "named arguments must not be dropped");
    }

    [Fact(DisplayName = "The outbox serializer writes inherited properties and reads a long-backed enum with GetInt64")]
    public void Outbox_serializer_covers_base_properties_and_wide_enums()
    {
        var run = Run(Usings + @"
[Flags] public enum Perm : long { None = 0, Big = 1L << 40 }
public abstract record DomainEvent : INotification { public Guid EventId { get; init; } }
[NotificationName(""order.created"")]
public sealed record OrderCreated : DomainEvent { public Perm Perm { get; init; } }
public sealed class OrderCreatedHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
        var serializer = run.Generated("GeneratedOutboxNotificationSerializer.g.cs");
        serializer.Should().Contain("EventId", "a base record's property is part of the payload");
        serializer.Should().Contain("GetInt64", "an enum backed by long must not be read with GetInt32");
    }

    [Fact(DisplayName = "A handler for a base notification type next to a derived one does not produce a subsumed switch arm")]
    public void Base_and_derived_notification_handlers_compile()
    {
        var run = Run(Usings + @"
public sealed record UserCreated : INotification;
public sealed class AuditAll : INotificationHandler<INotification>
{
    public Task Handle(INotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
public sealed class OnUserCreated : INotificationHandler<UserCreated>
{
    public Task Handle(UserCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
    }

    [Fact(DisplayName = "A concrete request deriving from another concrete request compiles and both are routed")]
    public void Derived_concrete_request_compiles()
    {
        var run = Run(Usings + @"
public class CreateUser : CommandBase;
public sealed class CreateAdmin : CreateUser;
public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
public sealed class CreateAdminHandler : ICommandHandler<CreateAdmin>
{
    public Task<CommandResult> Handle(CreateAdmin command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        run.CompileErrors.Should().BeEmpty("a type-pattern switch used to emit a subsumed arm (CS8510) here");
        var dispatcher = run.Generated("GeneratedRequestDispatcher.g.cs");
        dispatcher.Should().Contain("[typeof(global::ProbeNs.CreateUser)]").And.Contain("[typeof(global::ProbeNs.CreateAdmin)]");
    }

    [Fact(DisplayName = "AOT hints close a constrained open-generic behavior only over the requests that satisfy it")]
    public void Aot_hints_respect_generic_constraints()
    {
        var run = Run(Usings + @"
public sealed class Save : CommandBase;
public sealed class SaveHandler : ICommandHandler<Save>
{
    public Task<CommandResult> Handle(Save command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
public sealed class Count : QueryBase<int>;
public sealed class CountHandler : IQueryHandler<Count, int>
{
    public Task<int> Handle(Count query, CancellationToken cancellationToken) => Task.FromResult(0);
}
public sealed class CommandOnlyBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
    where TRequest : ICommand, IRequest<TResult>
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}");

        run.CompileErrors.Should().BeEmpty("a command-only behavior next to a query used to emit an invalid typeof (CS0311)");
        var hints = run.Generated("CqrsAotHints.g.cs");
        hints.Should().Contain("CommandOnlyBehavior<global::ProbeNs.Save,");
        hints.Should().NotContain("CommandOnlyBehavior<global::ProbeNs.Count,");
    }

    private static GeneratorRun Run(params string[] sources)
    {
        // The full trusted-platform list (not just the assemblies loaded so far): the emitted code must compile, so
        // every framework assembly it touches — System.Text.Json, the async-enumerable extensions — has to resolve.
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator);
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location);
        var references = platform.Concat(loaded)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "GeneratorProbe",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, path: $"Probe{i}.cs")),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new Gen().AsSourceGenerator(), new AotGen().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var result = driver.GetRunResult();
        var generated = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);

        var errors = updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} {d.Location.SourceTree?.FilePath}: {d.GetMessage()}")
            .ToArray();

        return new GeneratorRun(result.Diagnostics, errors, generated);
    }

    private sealed record GeneratorRun(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        string[] CompileErrors,
        IReadOnlyDictionary<string, string> Sources)
    {
        public string Generated(string hintName) =>
            Sources.TryGetValue(hintName, out var text)
                ? text
                : throw new InvalidOperationException($"'{hintName}' was not generated. Generated: {string.Join(", ", Sources.Keys)}");
    }
}

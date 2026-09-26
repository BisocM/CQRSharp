using CQRSharp.Tests.Shared;
using FluentAssertions;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Runs the source generator over legal user code and then compiles what it emitted: the generator must never turn a
///     valid project into a broken build, nor silently drop a binding. Each case pins a shape that could do one of those.
/// </summary>
public sealed class GeneratedCodeCompilesTests
{
    private const string Usings = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
using CQRSharp.Pipelines;
using CQRSharp.Core.Pipelines;
using CQRSharp.Tests.Shared;

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

    [Fact(DisplayName = "Attribute constants the compiler accepts compile in generated code: negative and keyword enum values, NaN and infinities, an open-generic typeof")]
    public void Attribute_edge_constants_compile()
    {
        var run = Run(Usings + @"
public enum Level { Low = 0 }
public enum Mode { @fixed = 1, @class = 2 }
public sealed class TuneAttribute : Attribute, IPreHandlerAttribute
{
    public TuneAttribute(Level level, Mode mode, double ratio, float scale, Type pipeline) { }
    public double Limit { get; set; }
    public int PreHandlerExecutionPriority => 0;
    public Task OnBeforeHandle(IRequest request, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}
public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}

[Tune((Level)(-1), Mode.@fixed, double.NaN, float.NegativeInfinity, typeof(Audit<,>), Limit = double.PositiveInfinity)]
[PipelineExemption(typeof(Audit<,>))]
public sealed class Charge : CommandBase;
public sealed class ChargeHandler : ICommandHandler<Charge>
{
    public Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN016");
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("((global::ProbeNs.Level)(-1))", "a negative value without a member is cast in parentheses, never read as a subtraction");
        module.Should().Contain("global::ProbeNs.Mode.@fixed", "a keyword-named member is escaped");
        module.Should().Contain("double.NaN").And.Contain("float.NegativeInfinity").And.Contain("Limit = double.PositiveInfinity");
        module.Should().Contain("typeof(global::ProbeNs.Audit<,>)");
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

    [Fact(DisplayName = "A stable-named notification deriving from another stable-named notification compiles: the serializer dispatches on the exact type")]
    public void Derived_stable_named_notifications_compile()
    {
        var run = Run(Usings + @"
[NotificationName(""order.event"")]
public record OrderEvent : INotification { public Guid OrderId { get; init; } }
[NotificationName(""order.event.shipped"")]
public sealed record OrderShipped : OrderEvent { public string Carrier { get; init; } = """"; }
public sealed class OrderEventHandler : INotificationHandler<OrderEvent>
{
    public Task Handle(OrderEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
        run.Generated("GeneratedOutboxNotificationSerializer.g.cs").Should().Contain("OrderShipped");
    }

    [Fact(DisplayName = "A payload property named like a C# keyword compiles: the generated serializer escapes it")]
    public void Keyword_named_properties_compile()
    {
        var run = Run(Usings + @"
[NotificationName(""audit.event"")]
public sealed record AuditEvent : INotification { public string @event { get; init; } = """"; public int @class { get; init; } }
public sealed class AuditEventHandler : INotificationHandler<AuditEvent>
{
    public Task Handle(AuditEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
    }

    [Fact(DisplayName = "An assembly with only a validator for a request declared elsewhere still gets a module that registers it")]
    public void Validator_only_assembly_gets_a_module()
    {
        var run = Run(Usings + @"
public sealed class ExternalCommandValidator : IRequestValidator<global::CQRSharp.Tests.Shared.TestCommand>
{
    public Task<ValidationFailure[]> ValidateAsync(global::CQRSharp.Tests.Shared.TestCommand request, CancellationToken cancellationToken)
        => Task.FromResult(Array.Empty<ValidationFailure>());
}");

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain("IRequestValidator<", "the validator is registered through its interface forwarder");
    }

    [Fact(DisplayName = "A request context factory is registered by the generator under its factory interface")]
    public void Context_factories_are_registered()
    {
        var run = Run(Usings + @"
public sealed class TenantContext : RequestContextBase { public string Tenant { get; init; } = """"; }
public sealed class TenantContextFactory : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new TenantContext());
}");

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("global::CQRSharp.Core.Registries.DiscoveredContextFactories.Register<global::ProbeNs.TenantContext, global::ProbeNs.TenantContextFactory>(services);");
        module.Should().Contain("[typeof(global::ProbeNs.TenantContext)] = global::CQRSharp.Core.Registries.RequestContextSource.For<global::ProbeNs.TenantContext>()");
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

        run.CompileErrors.Should().BeEmpty("routing a derived request beside its base must not emit a subsumed type-pattern arm (CS8510)");
        run.Generated("CqrsModule.g.cs")
            .Should().Contain("[typeof(global::ProbeNs.CreateUser)] = global::CQRSharp.Core.Pipelines.RequestRoute.Command<global::ProbeNs.CreateUser>()")
            .And.Contain("[typeof(global::ProbeNs.CreateAdmin)] = global::CQRSharp.Core.Pipelines.RequestRoute.Command<global::ProbeNs.CreateAdmin>()");
    }

    [Fact(DisplayName = "Every notification handler becomes a named subscription: the default name is the namespace-qualified type name, [NotificationHandlerName] pins it")]
    public void Subscriptions_are_emitted_with_stable_names()
    {
        var run = Run(Usings + @"
[NotificationName(""order.created"")]
public sealed record OrderCreated(Guid OrderId) : INotification;
public sealed class UpdateInventory : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
[NotificationHandlerName(""billing.invoice"")]
public sealed class SendInvoice : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain(@"NotificationSubscription.For<global::ProbeNs.UpdateInventory, global::ProbeNs.OrderCreated>(""ProbeNs.UpdateInventory"")");
        module.Should().Contain(@"NotificationSubscription.For<global::ProbeNs.SendInvoice, global::ProbeNs.OrderCreated>(""billing.invoice"")");
    }

    [Fact(DisplayName = "PartitionBy emits a typed partition key selector over the named property")]
    public void PartitionBy_emits_a_selector()
    {
        var run = Run(Usings + @"
public abstract record DomainEvent : INotification { public Guid AggregateId { get; init; } }
[NotificationName(""order.created"", PartitionBy = nameof(AggregateId))]
public sealed record OrderCreated : DomainEvent;
public sealed class OrderCreatedHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN011", "an inherited property is a readable property");
        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain(
            "[typeof(global::ProbeNs.OrderCreated)] = static n => global::CQRSharp.Core.Outbox.OutboxPartitionKey.From(((global::ProbeNs.OrderCreated)n).AggregateId),");
    }

    [Fact(DisplayName = "A handler declared for a base type subscribes under that type and compiles")]
    public void Base_type_handler_subscription_compiles()
    {
        var run = Run(Usings + @"
[NotificationName(""user.created"")]
public sealed record UserCreated(Guid Id) : INotification;
public sealed class AuditAll : INotificationHandler<INotification>
{
    public Task Handle(INotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain(@"NotificationSubscription.For<global::ProbeNs.AuditAll, global::CQRSharp.INotification>(""ProbeNs.AuditAll"")");
    }

    [Fact(DisplayName = "An idempotent request gets a generated, write-only fingerprint that skips the context and compiles")]
    public void Idempotent_request_fingerprint_is_emitted()
    {
        var run = Run(Usings + @"
public sealed record Line(string Sku, int Quantity);
public sealed class PlaceOrder : CommandBase, IIdempotentRequest
{
    public Guid CustomerId { get; init; }
    public Line[] Lines { get; init; } = Array.Empty<Line>();
    public string IdempotencyKey => $""order:{CustomerId}"";   // computed, get-only: fine for a write-only rendering
}
public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrder>
{
    public Task<CommandResult> Handle(PlaceOrder command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN014");
        run.CompileErrors.Should().BeEmpty();
        var fingerprinter = run.Generated("GeneratedRequestFingerprinter.g.cs");
        fingerprinter.Should().Contain("typeof(global::ProbeNs.PlaceOrder)");
        fingerprinter.Should().Contain("SHA256.HashData");
        fingerprinter.Should().Contain("\"customerId\"").And.Contain("\"lines\"").And.Contain("\"idempotencyKey\"");
        fingerprinter.Should().NotContain("\"context\"");
        fingerprinter.Should().NotContain("ReadObj_", "a fingerprint never deserializes");
        run.Generated("CqrsModule.g.cs").Should().Contain("RequestFingerprinter => GeneratedRequestFingerprinter.Instance");
    }

    [Fact(DisplayName = "A generic context factory type still maps its context in the registry and the marker, without a service registration")]
    public void Generic_factory_type_keeps_its_mapping()
    {
        var run = Run(new[]
        {
            Usings + @"
public sealed class Ctx1 : RequestContextBase;
public sealed class GenFactory<T> : IRequestContextFactory<Ctx1>
{
    public ValueTask<Ctx1> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new Ctx1());
}"
        });

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("[typeof(global::ProbeNs.Ctx1)] = global::CQRSharp.Core.Registries.RequestContextSource.For<global::ProbeNs.Ctx1>()");
        module.Should().NotContain("typeof(global::ProbeNs.GenFactory<");
        run.Generated("CqrsGeneratedAssemblyMarkers.g.cs").Should().Contain("CqrsRegisteredContextFactory(typeof(global::ProbeNs.Ctx1))");
    }

    [Fact(DisplayName = "A handler bound to a private nested notification gets CQRGEN010 and no forwarder that would not compile")]
    public void Inaccessible_binding_gets_no_forwarder()
    {
        var run = Run(new[]
        {
            Usings + @"
public class Outer
{
    private sealed record Hidden : INotification;
    internal sealed class HiddenHandler : INotificationHandler<Hidden>
    {
        Task INotificationHandler<Hidden>.Handle(Hidden notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
public sealed record Visible : INotification;
public sealed class VisibleHandler : INotificationHandler<Visible>
{
    public Task Handle(Visible notification, CancellationToken cancellationToken) => Task.CompletedTask;
}"
        });

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().Contain(d => d.Id == "CQRGEN010");
    }

    [Fact(DisplayName = "A required member the selected constructor supplies is still set in the generated initializer")]
    public void Required_constructor_member_compiles()
    {
        var run = Run(new[]
        {
            Usings + @"
[NotificationName(""req-evt"")]
public sealed class ReqEvt : INotification
{
    public ReqEvt(Guid id) { Id = id; }
    public required Guid Id { get; init; }
    public string Name { get; init; } = """";
}
public sealed class ReqEvtHandler : INotificationHandler<ReqEvt>
{
    public Task Handle(ReqEvt notification, CancellationToken cancellationToken) => Task.CompletedTask;
}"
        });

        run.CompileErrors.Should().BeEmpty();
        run.Generated("GeneratedOutboxNotificationSerializer.g.cs").Should().MatchRegex(@"Id = __m\d+ \}");
    }

    [Fact(DisplayName = "A request with a value-type result gets closed behavior factories for every open-generic behavior in view")]
    public void Value_type_results_get_closed_behavior_factories()
    {
        var run = Run(new[]
        {
            Usings + @"
public sealed class CountQuery : QueryBase<int>;
public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
{
    public Task<int> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}
public sealed class NameQuery : QueryBase<string>;
public sealed class NameQueryHandler : IQueryHandler<NameQuery, string>
{
    public Task<string> Handle(NameQuery query, CancellationToken cancellationToken) => Task.FromResult(""n"");
}
public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}"
        });

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("new global::CQRSharp.Core.Pipelines.ClosedBehaviorCatalog(__closedBehaviors)");
        module.Should().Contain("typeof(global::CQRSharp.Pipelines.LoggingBehavior<,>), typeof(global::CQRSharp.Pipelines.IPipelineBehavior<global::ProbeNs.CountQuery, int>)");
        module.Should().Contain("ActivatorUtilities.CreateInstance<global::CQRSharp.Pipelines.LoggingBehavior<global::ProbeNs.CountQuery, int>>(sp)");
        module.Should().Contain("ActivatorUtilities.CreateInstance<global::ProbeNs.Audit<global::ProbeNs.CountQuery, int>>(sp)");
        module.Should().NotContain("global::ProbeNs.NameQuery, string>>(sp)");
    }

    [Fact(DisplayName = "An open-generic behavior nested in a non-generic class is found and closed over a value-type result")]
    public void Nested_open_behavior_is_closed()
    {
        var run = Run(new[]
        {
            Usings + @"
public sealed class CountQuery : QueryBase<int>;
public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
{
    public Task<int> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}
public static class Behaviors
{
    public static class Inner
    {
        public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
        {
            public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
        }
    }
}"
        });

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain("ActivatorUtilities.CreateInstance<global::ProbeNs.Behaviors.Inner.Audit<global::ProbeNs.CountQuery, int>>(sp)");
    }

    [Fact(DisplayName = "Notification routes cover every concrete notification declared or handled, never an interface or an abstract type")]
    public void Notification_routes_cover_concrete_types_only()
    {
        var run = Run(new[]
        {
            Usings + @"
public abstract record DomainEvent : INotification;
public sealed record OrderPlaced : DomainEvent;
public sealed record Declared : INotification;
public sealed class AuditAll : INotificationHandler<INotification>, INotificationHandler<DomainEvent>, INotificationHandler<global::CQRSharp.Tests.Shared.TestNotification>
{
    public Task Handle(INotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Handle(DomainEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Handle(global::CQRSharp.Tests.Shared.TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}"
        });

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("[typeof(global::ProbeNs.OrderPlaced)] = global::CQRSharp.Core.Notifications.NotificationRoute.For<global::ProbeNs.OrderPlaced>()")
            .And.Contain("[typeof(global::ProbeNs.Declared)] = global::CQRSharp.Core.Notifications.NotificationRoute.For<global::ProbeNs.Declared>()")
            .And.Contain("NotificationRoute.For<global::CQRSharp.Tests.Shared.TestNotification>()", "a handled concrete type declared elsewhere gets a route too");
        module.Should().NotContain("NotificationRoute.For<global::CQRSharp.INotification>").And.NotContain("NotificationRoute.For<global::ProbeNs.DomainEvent>");
    }

    [Fact(DisplayName = "A notification handler is registered by its concrete type only, never as INotificationHandler<T>")]
    public void Notification_handler_gets_no_interface_forwarder()
    {
        var run = Run(new[]
        {
            Usings + @"
public sealed record OrderPlaced : INotification;
public sealed class OnOrderPlaced : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => Task.CompletedTask;
}"
        });

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("TryAddTransient(services, typeof(global::ProbeNs.OnOrderPlaced))");
        module.Should().NotContain("ServiceDescriptor.Transient<global::CQRSharp.INotificationHandler<");
    }

    [Fact(DisplayName = "The module is internal: only its registrar is public, and hidden from IntelliSense")]
    public void Module_types_are_not_public_api()
    {
        var run = Run(Usings + @"
public sealed record Ping : INotification;
public sealed class OnPing : INotificationHandler<Ping>
{
    public Task Handle(Ping notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("internal sealed class CqrsModule : global::CQRSharp.Core.Modules.ICqrsModule");
        module.Should().Contain("public static class CqrsModuleRegistrar");
        run.Output.GetTypeByMetadataName("CQRSharp.Generated.GeneratorProbe.CqrsModuleRegistrar")!.GetAttributes()
            .Should().Contain(a => a.AttributeClass!.Name == "EditorBrowsableAttribute");
        run.Output.GetTypeByMetadataName("CQRSharp.Generated.GeneratorProbe.CqrsModule")!.DeclaredAccessibility.Should().Be(Microsoft.CodeAnalysis.Accessibility.Internal);
        run.Sources.Keys.Should().NotContain(["GeneratedRequestDispatcher.g.cs", "GeneratedStreamRequestDispatcher.g.cs", "GeneratedDirectNotificationDispatcher.g.cs", "GeneratedCqrsDiagnostics.g.cs"]);
    }

    [Fact(DisplayName = "A closed generic request and a request from a contracts-only assembly are routed, so they dispatch and are described")]
    public void Requests_declared_elsewhere_are_routed()
    {
        var contracts = ContractsReference(@"
using CQRSharp;

namespace Contracts;

public sealed class GetPrice : QueryBase<decimal> { public string Sku { get; init; } = """"; }
public sealed class Box<T> : QueryBase<T> { public T Value { get; init; } = default!; }
");
        var run = CompilationHarness.RunGenerators([Usings + @"
public sealed class GetPriceHandler : IQueryHandler<global::Contracts.GetPrice, decimal>
{
    public Task<decimal> Handle(global::Contracts.GetPrice query, CancellationToken cancellationToken) => Task.FromResult(1m);
}
public sealed class BoxHandler : IQueryHandler<global::Contracts.Box<int>, int>
{
    public Task<int> Handle(global::Contracts.Box<int> query, CancellationToken cancellationToken) => Task.FromResult(query.Value);
}"], [contracts]);

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs")
            .Should().Contain("[typeof(global::Contracts.GetPrice)] = global::CQRSharp.Core.Pipelines.RequestRoute.Query<global::Contracts.GetPrice, decimal>()")
            .And.Contain("[typeof(global::Contracts.Box<int>)] = global::CQRSharp.Core.Pipelines.RequestRoute.Query<global::Contracts.Box<int>, int>()");
    }

    [Fact(DisplayName = "An idempotent request declared where the generator does not run, or closed generic, is fingerprinted where it is handled")]
    public void Idempotent_requests_declared_elsewhere_are_fingerprinted_at_the_handler()
    {
        var contracts = ContractsReference(@"
using CQRSharp;

namespace Contracts;

public sealed class Refund : CommandBase, IIdempotentRequest
{
    public string IdempotencyKey { get; init; } = """";
    public decimal Amount { get; init; }
}
public sealed class Charge<TAmount> : CommandBase, IIdempotentRequest
{
    public string IdempotencyKey { get; init; } = """";
    public TAmount Amount { get; init; } = default!;
}
");
        var run = CompilationHarness.RunGenerators([Usings + @"
public sealed class RefundHandler : ICommandHandler<global::Contracts.Refund>
{
    public Task<CommandResult> Handle(global::Contracts.Refund command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
public sealed class ChargeHandler : ICommandHandler<global::Contracts.Charge<decimal>>
{
    public Task<CommandResult> Handle(global::Contracts.Charge<decimal> command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}"], [contracts]);

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN014");
        run.Generated("GeneratedRequestFingerprinter.g.cs")
            .Should().Contain("typeof(global::Contracts.Refund)")
            .And.Contain("typeof(global::Contracts.Charge<decimal>)");
    }

    // A contracts project: it references CQRSharp.Abstractions alone, so the generator does not run in it.
    private static Microsoft.CodeAnalysis.MetadataReference ContractsReference(string source)
    {
        var compilation = CompilationHarness.Compile([source], "Contracts", ProbeReferences.AbstractionsOnly());
        using var stream = new MemoryStream();
        compilation.Emit(stream).Success.Should().BeTrue();
        return Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(stream.ToArray());
    }

    [Fact(DisplayName = "The generated fingerprint includes the request type, so same-shaped requests never share one")]
    public void Fingerprint_names_the_request_type()
    {
        var run = Run(new[]
        {
            Usings + @"
public sealed class CancelOrder : CommandBase, IIdempotentRequest { public string OrderId { get; init; } = """"; public string IdempotencyKey { get; init; } = """"; }
public sealed class CancelOrderHandler : ICommandHandler<CancelOrder>
{
    public Task<CommandResult> Handle(CancelOrder command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}"
        });

        run.CompileErrors.Should().BeEmpty();
        var fingerprinter = run.Generated("GeneratedRequestFingerprinter.g.cs");
        fingerprinter.Should().Contain("Hash(\"ProbeNs.CancelOrder\"").And.Contain("writer.WriteString(\"$type\", requestType)");
    }

    [Fact(DisplayName = "Closed behaviors: constraints are checked as the compiler checks them; a behavior generated code cannot close is recorded as a gap")]
    public void Closed_behaviors_respect_constraints_and_skip_unusable_behaviors()
    {
        var run = Run(new[]
        {
            Usings + @"
public sealed class NullableQ : QueryBase<int?>;
public sealed class NullableQHandler : IQueryHandler<NullableQ, int?>
{
    public Task<int?> Handle(NullableQ query, CancellationToken cancellationToken) => Task.FromResult<int?>(1);
}
public struct Payload { public string Name; }
public sealed class PayloadQ : QueryBase<Payload>;
public sealed class PayloadQHandler : IQueryHandler<PayloadQ, Payload>
{
    public Task<Payload> Handle(PayloadQ query, CancellationToken cancellationToken) => Task.FromResult(default(Payload));
}
public sealed class StructOnly<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest where TResult : struct
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}
public sealed class UnmanagedOnly<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest where TResult : unmanaged
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}
public sealed class Comparable<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest where TResult : IComparable
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}
[Obsolete(""old"")]
public sealed class OldBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}
public class Outer<X>
{
    public sealed class NestedBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }
}
public sealed class Nums : StreamRequestBase<int>;
public sealed class NumsHandler : IStreamRequestHandler<Nums, int>
{
    public async System.Collections.Generic.IAsyncEnumerable<int> Handle(Nums request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) { await Task.Yield(); yield return 1; }
}
public sealed class Both<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IStreamPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    public System.Collections.Generic.IAsyncEnumerable<TResult> Handle(TRequest request, StreamHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}"
        });

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("CreateInstance<global::ProbeNs.OldBehavior<global::ProbeNs.PayloadQ, global::ProbeNs.Payload>>(sp)",
            "an [Obsolete] warning does not stop generated code from naming the behavior");
        module.Should().NotContain("CreateInstance<global::ProbeNs.Outer<").And.Contain(
            "new global::CQRSharp.Core.Pipelines.ClosedBehaviorGap(typeof(global::CQRSharp.Pipelines.IPipelineBehavior<global::ProbeNs.PayloadQ, global::ProbeNs.Payload>), \"ProbeNs.Outer`1+NestedBehavior`2\", \"GeneratorProbe\", \"it is nested in a generic type\")");
        run.Output.GetDiagnostics(TestContext.Current.CancellationToken).Should().NotContain(d => d.Id == "CS0618", "the closing code suppresses the obsolete warning it would raise");
        module.Should().NotContain("StructOnly<global::ProbeNs.NullableQ").And.NotContain("Comparable<global::ProbeNs.NullableQ");
        module.Should().NotContain("UnmanagedOnly<global::ProbeNs.PayloadQ");
        module.Should().Contain("typeof(global::CQRSharp.Pipelines.IPipelineBehavior<global::ProbeNs.PayloadQ, global::ProbeNs.Payload>)");
        module.Should().Contain("typeof(global::ProbeNs.Both<,>), typeof(global::CQRSharp.Pipelines.IStreamPipelineBehavior<global::ProbeNs.Nums, int>)");
    }

    [Fact(DisplayName = "A notification with a required field is reported (CQRGEN005) instead of emitting a reader that cannot compile")]
    public void Required_field_is_reported()
    {
        var run = Run(new[]
        {
            Usings + @"
[NotificationName(""evt.f"")]
public sealed class EvtF : INotification { public required string Tag; }
public sealed class EvtFHandler : INotificationHandler<EvtF>
{
    public Task Handle(EvtF notification, CancellationToken cancellationToken) => Task.CompletedTask;
}"
        });

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().Contain(d => d.Id == "CQRGEN005");
    }

    [Fact(DisplayName = "A request whose result generated code cannot name is not routed: CQRGEN010 at the request and at its handler, and the project compiles")]
    public void Inaccessible_result_types_are_reported_not_emitted()
    {
        var run = Run(Usings + @"
public static class Feature
{
    private sealed record Row(int X);
    private readonly record struct Id(int Value);

    public sealed class Query : IQuery<Row> { public IRequestContext? Context { get; set; } }
    public sealed class ValueQuery : IQuery<Id> { public IRequestContext? Context { get; set; } }
    public sealed class Create : ICommand<Row> { public IRequestContext? Context { get; set; } }

    public sealed class QueryHandler : IQueryHandler<Query, Row>
    {
        Task<Row> IQueryHandler<Query, Row>.Handle(Query query, CancellationToken cancellationToken) => Task.FromResult(new Row(1));
    }
}");

        run.CompileErrors.Should().BeEmpty("a class may implement a less accessible interface, and the generated code must not trip over it");
        var reported = run.GeneratorDiagnostics.Where(d => d.Id == "CQRGEN010").Select(d => d.GetMessage()).ToArray();
        reported.Should().Contain(m => m.Contains("Feature.Query'") && m.Contains("Feature.Row"));
        reported.Should().Contain(m => m.Contains("Feature.ValueQuery'") && m.Contains("Feature.Id"));
        reported.Should().Contain(m => m.Contains("Feature.Create'") && m.Contains("Feature.Row"));
        reported.Should().Contain(m => m.Contains("Feature.QueryHandler'") && m.Contains("Feature.Row"));
        run.Sources.Values.Should().NotContain(source => source.Contains("Feature.Row") || source.Contains("Feature.Id"),
            "nothing generated may name a type it cannot see");
    }

    [Fact(DisplayName = "Types in the global namespace named like framework types cannot capture a name in any generated file")]
    public void Global_namespace_types_do_not_capture_generated_names()
    {
        var run = Run(@"
using System.Threading;
using CQRSharp;

public sealed class Task;
public enum Type { Small, Large }
public sealed class Exception;
public delegate void Delegate();
public sealed class Func;
public sealed class Array;
public sealed class Action;
public sealed class Buffer;
public sealed class Convert;
public sealed class IServiceCollection;
public sealed class ServiceDescriptor;
public sealed class Utf8JsonWriter;
public sealed class Utf8JsonReader;
public sealed class JsonException;
public sealed class JsonTokenType;
public sealed class ArgumentNullException;
public sealed class InvalidOperationException;
public sealed class CultureInfo;
public sealed class ArrayBufferWriter;
public sealed class SHA256;
public sealed class CqrsModule;

namespace App
{
    public sealed class Ping : CommandBase;
    public sealed class PingHandler : ICommandHandler<Ping>
    {
        public System.Threading.Tasks.Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(CommandResult.FromSuccess());
    }

    public sealed class PingValidator : IRequestValidator<Ping>
    {
        public System.Threading.Tasks.Task<ValidationFailure[]> ValidateAsync(Ping request, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(System.Array.Empty<ValidationFailure>());
    }

    public sealed class OnPingFailed : IRequestExceptionAction<Ping, System.InvalidOperationException>
    {
        public System.Threading.Tasks.Task Execute(Ping request, System.InvalidOperationException exception, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    public sealed class Count : QueryBase<int>;
    public sealed class CountHandler : IQueryHandler<Count, int>
    {
        public System.Threading.Tasks.Task<int> Handle(Count query, CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(1);
    }

    public sealed class Numbers : StreamRequestBase<int>;
    public sealed class NumbersHandler : IStreamRequestHandler<Numbers, int>
    {
        public async System.Collections.Generic.IAsyncEnumerable<int> Handle(Numbers request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await System.Threading.Tasks.Task.Yield();
            yield return 1;
        }
    }

    [NotificationName(""app.happened"")]
    public sealed record Happened(
        System.Guid Id,
        char Grade,
        System.TimeSpan Took,
        System.DateOnly Day,
        System.TimeOnly At,
        System.Uri Link,
        System.Collections.Generic.Dictionary<string, int> Counts,
        System.Collections.Generic.HashSet<string> Tags) : INotification;

    public sealed class OnHappened : INotificationHandler<Happened>
    {
        public System.Threading.Tasks.Task Handle(Happened notification, CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
    }

    public sealed class Charge : CommandBase, IIdempotentRequest
    {
        public string IdempotencyKey { get; init; } = """";
        public System.Collections.Generic.Dictionary<string, decimal> Lines { get; init; } = new();
        public System.Collections.Generic.HashSet<int> Codes { get; init; } = new();
    }

    public sealed class ChargeHandler : ICommandHandler<Charge>
    {
        public System.Threading.Tasks.Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(CommandResult.FromSuccess());
    }

    public static class Wiring
    {
        public static void Wire(Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services.AddCqrsGenerated();
    }
}");

        run.CompileErrors.Should().BeEmpty();
        run.Sources.Keys.Should().Contain(["CqrsModule.g.cs", "GeneratedOutboxNotificationSerializer.g.cs", "GeneratedRequestFingerprinter.g.cs", "CqrsGeneratedBootstrap.g.cs"]);
        GeneratedWarnings(run).Should().BeEmpty();
    }

    [Fact(DisplayName = "An attribute on a base request applies to the derived request the way GetCustomAttributes(inherit: true) reads it")]
    public void Base_request_attributes_are_inherited()
    {
        var run = Run(Usings + @"
public class AuditAttribute(string tag) : Attribute, IPreHandlerAttribute
{
    public string Tag { get; } = tag;
    public int PreHandlerExecutionPriority => 0;
    public Task OnBeforeHandle(IRequest request, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LocalOnlyAttribute : Attribute, IPreHandlerAttribute
{
    public int PreHandlerExecutionPriority => 0;
    public Task OnBeforeHandle(IRequest request, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}

// AttributeUsage is inherited from AuditAttribute's defaults: single use, inherited.
public sealed class TracedAttribute(string tag) : AuditAttribute(tag);

public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}

[Audit(""base"")]
[Traced(""base-traced"")]
[LocalOnly]
[PipelineExemption(typeof(Audit<,>))]
public abstract class AuditedCommand : CommandBase;

[Traced(""derived-traced"")]
public sealed class CreateUser : AuditedCommand;

public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        run.CompileErrors.Should().BeEmpty();
        var metadata = run.Generated("CqrsModule.g.cs").Split('\n').Single(line => line.Contains("[typeof(global::ProbeNs.CreateUser)] = new global::CQRSharp.Core.Registries.RequestMetadata("));
        metadata.Should().Contain(@"new global::ProbeNs.AuditAttribute(""base"")", "an attribute on a base request class is inherited");
        metadata.Should().Contain(@"new global::ProbeNs.TracedAttribute(""derived-traced"")").And.NotContain("base-traced",
            "a single-use attribute the derived request also carries is the derived one");
        metadata.Should().NotContain("LocalOnlyAttribute", "an attribute declared Inherited = false stays on the class that carries it");
        metadata.Should().Contain("new global::CQRSharp.PipelineExemptionAttribute(typeof(global::ProbeNs.Audit<,>))");
    }

    [Fact(DisplayName = "A base request in a referenced assembly passes its attributes on, and one generated code cannot rebuild is CQRGEN016")]
    public void Referenced_base_request_attributes_are_inherited()
    {
        var library = CompilationHarness.RunGenerators([@"
using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;

namespace Lib;

public sealed class AuditAttribute(string tag) : Attribute, IPreHandlerAttribute
{
    public int PreHandlerExecutionPriority => 0;
    public Task OnBeforeHandle(IRequest request, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class HiddenAttribute : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => 0;
    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}

[Audit(""library"")]
public abstract class AuditedCommand : CommandBase;

[Hidden]
public abstract class GuardedCommand : CommandBase;"], assemblyName: "Lib");
        library.CompileErrors.Should().BeEmpty();

        var run = CompilationHarness.RunGenerators([Usings + @"
public sealed class CreateUser : global::Lib.AuditedCommand;
public sealed class CreateUserHandler : ICommandHandler<CreateUser>
{
    public Task<CommandResult> Handle(CreateUser command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
public sealed class DeleteUser : global::Lib.GuardedCommand;
public sealed class DeleteUserHandler : ICommandHandler<DeleteUser>
{
    public Task<CommandResult> Handle(DeleteUser command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}"], [library.Reference!]);

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain(@"new global::Lib.AuditAttribute(""library"")").And.NotContain("HiddenAttribute");
        run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN016")
            .Which.Should().Match<Microsoft.CodeAnalysis.Diagnostic>(d =>
                d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error && d.GetMessage().Contains("DeleteUser") && d.GetMessage().Contains("Lib.HiddenAttribute"));
    }

    [Fact(DisplayName = "An [Obsolete] behavior is closed with its warnings suppressed; one marked as an error, or nested in a generic type, is a gap")]
    public void Obsolete_behaviors_are_closed_and_unclosable_ones_are_gaps()
    {
        var run = Run(Usings + @"
public sealed class CountQuery : QueryBase<int>;
public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
{
    public Task<int> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}
[Obsolete(""old"", DiagnosticId = ""OBS42"")]
public sealed class Legacy<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}
[Obsolete(""gone"", true)]
public sealed class Removed<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}");

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("#pragma warning disable CS0612, CS0618, OBS42")
            .And.Contain("CreateInstance<global::ProbeNs.Legacy<global::ProbeNs.CountQuery, int>>(sp)");
        module.Should().Contain(@"""ProbeNs.Removed`2"", ""GeneratorProbe"", ""it is marked [Obsolete] as an error""");
        GeneratedWarnings(run).Should().BeEmpty("the closing code suppresses the warnings naming an obsolete behavior raises");
    }

    [Fact(DisplayName = "Deprecated and experimental requests, handlers and notifications compile in generated code with their diagnostics suppressed; a handler obsolete as an error is skipped (CQRGEN006)")]
    public void Obsolete_and_experimental_consumer_types_compile_in_generated_code()
    {
        var run = Run(Usings + @"
[Obsolete(""old"")]
public sealed class OldCommand : CommandBase;
[Obsolete(""old"", DiagnosticId = ""OBS1"")]
public sealed class OldCommandHandler : ICommandHandler<OldCommand>
{
    public Task<CommandResult> Handle(OldCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
[Obsolete(""old"")]
[NotificationName(""probe.old"")]
public sealed record OldEvent(int Id) : INotification;
[Obsolete(""old"")]
public sealed class OldEventHandler : INotificationHandler<OldEvent>
{
    public Task Handle(OldEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
[System.Diagnostics.CodeAnalysis.Experimental(""EXP001"")]
public sealed class PreviewQuery : QueryBase<int>, IIdempotentRequest
{
    public string IdempotencyKey { get; init; } = """";
}
[System.Diagnostics.CodeAnalysis.Experimental(""EXP001"")]
public sealed class PreviewQueryHandler : IQueryHandler<PreviewQuery, int>
{
    public Task<int> Handle(PreviewQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}
public sealed class RemovedCommand : CommandBase;
[Obsolete(""gone"", true)]
public sealed class RemovedCommandHandler : ICommandHandler<RemovedCommand>
{
    public Task<CommandResult> Handle(RemovedCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        run.CompileErrors.Should().BeEmpty("an experimental type named by generated code is an error unless suppressed");
        GeneratedWarnings(run).Should().BeEmpty("generated code suppresses what naming a deprecated type raises");
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("#pragma warning disable CS0612, CS0618, EXP001, OBS1")
            .And.Contain("global::ProbeNs.OldCommandHandler").And.Contain("global::ProbeNs.PreviewQueryHandler");
        run.Generated("GeneratedOutboxNotificationSerializer.g.cs").Should().Contain("#pragma warning disable CS0612, CS0618, EXP001, OBS1");
        run.Generated("CqrsGeneratedAssemblyMarkers.g.cs").Should().Contain("#pragma warning disable CS0612, CS0618, EXP001, OBS1");
        module.Should().NotContain("RemovedCommandHandler", "CS0619 cannot be suppressed, so the handler is not named");
        run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN006")
            .Which.GetMessage().Should().Contain("RemovedCommandHandler").And.Contain("[Obsolete] as an error");
    }

    [Fact(DisplayName = "An open behavior constrained notnull is closed over a nullable value-type result without CS8714 in the generated module")]
    public void NotNull_behavior_closed_over_a_nullable_result_compiles_without_warnings()
    {
        var run = Run(Usings + @"
public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest where TResult : notnull
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}
public sealed class StreamAudit<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem> where TRequest : IRequest where TItem : notnull
{
    public System.Collections.Generic.IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, CancellationToken cancellationToken) => next(cancellationToken);
}
public sealed class GetMaybe : QueryBase<int?>;
public sealed class GetMaybeHandler : IQueryHandler<GetMaybe, int?>
{
    public Task<int?> Handle(GetMaybe query, CancellationToken cancellationToken) => Task.FromResult<int?>(null);
}
public sealed class MaybeItems : StreamRequestBase<int?>;
public sealed class MaybeItemsHandler : IStreamRequestHandler<MaybeItems, int?>
{
    public async System.Collections.Generic.IAsyncEnumerable<int?> Handle(MaybeItems request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) { await Task.Yield(); yield return null; }
}");

        run.CompileErrors.Should().BeEmpty();
        GeneratedWarnings(run).Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("CreateInstance<global::ProbeNs.Audit<global::ProbeNs.GetMaybe, int?>>(sp)", "the container ignores notnull, so the behavior applies");
        module.Should().Contain("CreateInstance<global::ProbeNs.StreamAudit<global::ProbeNs.MaybeItems, int?>>(sp)");
    }

    [Fact(DisplayName = "A module whose only closed-behavior entries are gaps compiles without a warning")]
    public void Gaps_only_module_has_no_warnings()
    {
        var run = CompilationHarness.RunGenerators(
            [Usings.Replace("using CQRSharp.Tests.Shared;", string.Empty) + @"
public sealed class CountQuery : QueryBase<int>;
public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
{
    public Task<int> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}
[Obsolete(""gone"", true)]
public sealed class Removed<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}"],
            references: ProbeReferences.Create(name => name is "CQRSharp.Pipelines.dll" or "CQRSharp.Tests.dll"));

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain("ClosedBehaviorGapCatalog(__closedBehaviorGaps)").And.NotContain("ClosedBehaviorFactory[]");
        GeneratedWarnings(run).Should().BeEmpty();
    }

    [Fact(DisplayName = "A referenced assembly's internal behavior is closed when it grants access (InternalsVisibleTo), and is a gap when it does not")]
    public void Internal_behaviors_of_referenced_assemblies()
    {
        const string usings = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
using CQRSharp.Pipelines;
";
        const string behavior = @"
namespace Lib
{
    internal sealed class TenantBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }
}";
        const string host = @"
public sealed class CountQuery : QueryBase<int>;
public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
{
    public Task<int> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}";

        var hidden = CompilationHarness.RunGenerators([usings + behavior], assemblyName: "Lib");
        var shared = CompilationHarness.RunGenerators([usings + @"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(""GeneratorProbe"")]" + behavior], assemblyName: "Lib");
        hidden.CompileErrors.Should().BeEmpty();
        shared.CompileErrors.Should().BeEmpty();

        var withoutAccess = CompilationHarness.RunGenerators([Usings + host], [hidden.Reference!]);
        withoutAccess.CompileErrors.Should().BeEmpty();
        withoutAccess.Generated("CqrsModule.g.cs").Should().Contain(
            @"new global::CQRSharp.Core.Pipelines.ClosedBehaviorGap(typeof(global::CQRSharp.Pipelines.IPipelineBehavior<global::ProbeNs.CountQuery, int>), ""Lib.TenantBehavior`2"", ""Lib"", ""it is internal to 'Lib', which does not grant 'GeneratorProbe' access (InternalsVisibleTo)"")");

        var withAccess = CompilationHarness.RunGenerators([Usings + host], [shared.Reference!]);
        withAccess.CompileErrors.Should().BeEmpty();
        withAccess.Generated("CqrsModule.g.cs").Should().Contain("CreateInstance<global::Lib.TenantBehavior<global::ProbeNs.CountQuery, int>>(sp)")
            .And.NotContain(@"""Lib.TenantBehavior`2""");
    }

    [Fact(DisplayName = "A host behavior over a referenced assembly's internal value-type request is a gap named by the request's runtime name")]
    public void Host_behaviors_over_inaccessible_referenced_requests_are_gaps()
    {
        var library = CompilationHarness.RunGenerators([@"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;

namespace Lib;

internal sealed class Secret : QueryBase<int>;
internal sealed class SecretHandler : IQueryHandler<Secret, int>
{
    public Task<int> Handle(Secret query, CancellationToken cancellationToken) => Task.FromResult(1);
}"], assemblyName: "Lib");
        library.CompileErrors.Should().BeEmpty();

        var run = CompilationHarness.RunGenerators([Usings + @"
public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}"], [library.Reference!]);

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsGeneratedBootstrap.g.cs").Should().Contain(
            @"new global::CQRSharp.Core.Pipelines.ClosedBehaviorGap(typeof(global::CQRSharp.Pipelines.IPipelineBehavior<,>), ""Lib.Secret"", ""Lib"", ""ProbeNs.Audit`2"", ""GeneratorProbe"", ""the request 'Lib.Secret' is not accessible to 'GeneratorProbe'"")");
    }

    [Fact(DisplayName = "A value-type notification gets closed factories for every open-generic notification behavior whose constraints admit it")]
    public void Value_type_notifications_get_closed_behavior_factories()
    {
        var run = Run(Usings + @"
public readonly struct Tick : INotification;
public sealed class OnTick : INotificationHandler<Tick>
{
    public Task Handle(Tick notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
public readonly record struct Declared(int Count) : INotification;
public sealed class Stamp<TNotification> : INotificationPipelineBehavior<TNotification> where TNotification : INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
}
public sealed class ReferencesOnly<TNotification> : INotificationPipelineBehavior<TNotification> where TNotification : class, INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
}
public sealed class Ping : INotification;");

        run.CompileErrors.Should().BeEmpty();
        var module = run.Generated("CqrsModule.g.cs");
        module.Should().Contain("new global::CQRSharp.Core.Pipelines.ClosedBehaviorFactory(typeof(global::ProbeNs.Stamp<>), typeof(global::CQRSharp.Pipelines.INotificationPipelineBehavior<global::ProbeNs.Tick>), " +
                                "static sp => global::Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<global::ProbeNs.Stamp<global::ProbeNs.Tick>>(sp))");
        module.Should().Contain("CreateInstance<global::ProbeNs.Stamp<global::ProbeNs.Declared>>(sp)", "a declared value-type notification is routed, and closed, without a handler");
        module.Should().Contain("[typeof(global::ProbeNs.Tick)] = global::CQRSharp.Core.Notifications.NotificationRoute.For<global::ProbeNs.Tick>()",
            "a plain struct notification is discovered like a record struct");
        module.Should().NotContain("ReferencesOnly<global::ProbeNs.Tick>", "its constraints exclude a value type");
        module.Should().NotContain("Stamp<global::ProbeNs.Ping>", "the container closes a behavior over a reference type itself");
    }

    [Fact(DisplayName = "A referenced assembly's value-type notifications get the host's notification behaviors in the bootstrap; one it cannot name is a gap")]
    public void Host_notification_behaviors_over_referenced_value_type_notifications()
    {
        var library = CompilationHarness.RunGenerators([@"
using CQRSharp;

namespace Lib;

public readonly struct Metered : INotification;
internal readonly struct Secret : INotification;"], assemblyName: "Lib");
        library.CompileErrors.Should().BeEmpty();

        var run = CompilationHarness.RunGenerators([Usings + @"
public sealed class Audit<TNotification> : INotificationPipelineBehavior<TNotification> where TNotification : INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
}"], [library.Reference!]);

        run.CompileErrors.Should().BeEmpty();
        var bootstrap = run.Generated("CqrsGeneratedBootstrap.g.cs");
        bootstrap.Should().Contain("CreateInstance<global::ProbeNs.Audit<global::Lib.Metered>>(sp)");
        bootstrap.Should().Contain(
            @"new global::CQRSharp.Core.Pipelines.ClosedBehaviorGap(typeof(global::CQRSharp.Pipelines.INotificationPipelineBehavior<>), ""Lib.Secret"", ""Lib"", ""ProbeNs.Audit`1"", ""GeneratorProbe"", ""the notification 'Lib.Secret' is not accessible to 'GeneratorProbe'"")");
    }

    [Fact(DisplayName = "A notification behavior generated code cannot close is recorded as a gap for a value-type notification it applies to")]
    public void Unclosable_notification_behaviors_are_gaps()
    {
        var run = Run(Usings + @"
public readonly struct Tick : INotification;
public static class Outer<T>
{
    public sealed class Nested<TNotification> : INotificationPipelineBehavior<TNotification> where TNotification : INotification
    {
        public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
    }
}");

        run.CompileErrors.Should().BeEmpty();
        run.Generated("CqrsModule.g.cs").Should().Contain(
            @"new global::CQRSharp.Core.Pipelines.ClosedBehaviorGap(typeof(global::CQRSharp.Pipelines.INotificationPipelineBehavior<global::ProbeNs.Tick>), ""ProbeNs.Outer`1+Nested`1"", ""GeneratorProbe"", ""it is nested in a generic type"")");
        GeneratedWarnings(run).Should().BeEmpty();
    }

    // The warnings the compiler reports in generated files: a consumer that builds with warnings as errors gets them as
    // errors, and cannot edit the code they are in.
    private static string[] GeneratedWarnings(GeneratorRun run)
        => run.Output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning && (d.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) ?? false))
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToArray();

    private static GeneratorRun Run(params string[] sources) => CompilationHarness.RunGenerators(sources);
}

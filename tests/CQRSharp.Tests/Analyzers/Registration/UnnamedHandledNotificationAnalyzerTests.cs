using System.Collections.Immutable;
using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     CQRA020: with the outbox turned on in a configuration all in view, under the generated serializer, a handled
///     notification class without [NotificationName] is never stored in the outbox. A suggestion, with a fix that names it.
/// </summary>
public sealed class UnnamedHandledNotificationAnalyzerTests
{
    private const string Notifications = """
                                         using System;
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         using CQRSharp;
                                         using CQRSharp.Pipelines;
                                         using CQRSharp.Persistence;
                                         using Microsoft.Extensions.DependencyInjection;

                                         namespace Shop.Orders
                                         {
                                             public sealed record OrderPlacedNotification(Guid OrderId) : INotification;
                                             [NotificationName("order.shipped")]
                                             public sealed record OrderShipped(Guid OrderId) : INotification;
                                             public sealed record OrderViewed(Guid OrderId) : INotification;
                                             public readonly record struct StockTicked(int Level) : INotification;
                                             public abstract record AuditEvent : INotification;
                                             public sealed record LoginAudited : AuditEvent;
                                             internal sealed class Internal
                                             {
                                                 private sealed record Hidden : INotification;
                                                 private sealed class HiddenHandler : INotificationHandler<Hidden>
                                                 {
                                                     public Task Handle(Hidden notification, CancellationToken cancellationToken) => Task.CompletedTask;
                                                 }
                                             }

                                             public sealed class OrderHandlers :
                                                 INotificationHandler<OrderPlacedNotification>, INotificationHandler<OrderShipped>,
                                                 INotificationHandler<StockTicked>, INotificationHandler<AuditEvent>
                                             {
                                                 public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
                                                 public Task Handle(OrderShipped notification, CancellationToken cancellationToken) => Task.CompletedTask;
                                                 public Task Handle(StockTicked notification, CancellationToken cancellationToken) => Task.CompletedTask;
                                                 public Task Handle(AuditEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
                                             }
                                         }

                                         """;

    private const string Serializer = """
                                      public sealed class AppSerializer : CQRSharp.Persistence.INotificationSerializer
                                      {
                                          public bool TryGetNotificationName(Type notificationType, out string? notificationName) { notificationName = null; return false; }
                                          public byte[] Serialize(CQRSharp.INotification notification) => [];
                                          public CQRSharp.INotification? Deserialize(string notificationName, byte[] payload) => null;
                                      }

                                      """;

    [Fact(DisplayName = "CQRA020: a handled notification class without [NotificationName] under a visible UseOutbox is reported at its declaration")]
    public async Task Handled_unnamed_notification_is_reported()
    {
        var diagnostics = await AnalyzeAsync("services.AddCqrsGenerated(b => b.UseLogging().UseOutbox(o => o.Transactional().UseInMemoryStore()))");

        var reported = diagnostics.Where(d => d.Id == "CQRA020").ToArray();
        reported.Select(d => d.GetMessage()).Should().BeEquivalentTo(
            [Message("OrderPlacedNotification"), Message("LoginAudited")],
            "a named, an unhandled, a value-type, an abstract and a hidden notification are not reported");
        reported.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Info);

        static string Message(string name) => $"'{name}' has handlers but no [NotificationName], so while the outbox is on (UseOutbox) it is never stored in the outbox and every publish of it is delivered in-process. Add [NotificationName(\"...\")] to make it durable, or leave it as it is if it is meant to stay in-process.";
    }

    [Theory(DisplayName = "CQRA020: nothing is reported without a visible outbox under the generated serializer")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging())", false)]
    [InlineData("services.AddCqrsGenerated()", false)]
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore())); services.AddNotificationSerializer<AppSerializer>()", true)]
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore())); services.AddSingleton<INotificationSerializer>(new AppSerializer())", true)]
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore()))", true)]
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore())); services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Disabled)", false)]
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore())); services.AddCqrsGenerated(b => b.UseLogging())", false)]
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => Configure(o)))", false)]
    public async Task Nothing_reported_unless_provable(string registration, bool declaresSerializer)
    {
        var diagnostics = await AnalyzeAsync(registration, declaresSerializer ? Serializer : "");

        diagnostics.Should().NotContain(d => d.Id == "CQRA020");
    }

    [Fact(DisplayName = "CQRA020 code fix: names the notification in the documented dotted form, then behind its namespace when that is taken")]
    public async Task Fix_adds_a_derived_NotificationName()
    {
        var result = await ApplyFixAsync(Notifications);

        result.Actions.Should().ContainSingle().Which.Title.Should().Be("Add [NotificationName(\"order.placed\")]");
        result.FixedSource.Should().Contain("""
                                                [NotificationName("order.placed")]
                                                public sealed record OrderPlacedNotification(Guid OrderId) : INotification;
                                            """);
        result.CompileErrors.Should().BeEmpty();
        result.RemainingDiagnostics.Should().BeEmpty();

        var taken = await ApplyFixAsync(Notifications.Replace("[NotificationName(\"order.shipped\")]", "[NotificationName(\"order.placed\")]"));

        taken.Actions.Should().ContainSingle().Which.Title.Should().Be("Add [NotificationName(\"shop.orders.order.placed\")]");
        taken.CompileErrors.Should().BeEmpty();
        taken.RemainingDiagnostics.Should().BeEmpty();
    }

    // The audit notifications are named, so the order notification is the one diagnostic to fix.
    private static Task<CodeFixResult> ApplyFixAsync(string notifications)
        => CodeFixHarness.ApplyAsync(
            notifications.Replace("public sealed record LoginAudited", "[NotificationName(\"audit.login\")] public sealed record LoginAudited") +
            Program("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore()))"),
            new UnnamedHandledNotificationAnalyzer(),
            new UnnamedHandledNotificationCodeFixProvider(),
            "CQRA020",
            withGeneratedCode: true,
            outputKind: OutputKind.ConsoleApplication,
            references: ProbeReferences.SingleProjectApplication());

    private static string Program(string registration)
        => "public static class Program { public static void Main() { var services = new ServiceCollection(); " + registration +
           "; } private static void Configure(OutboxStoreBuilder o) => o.UseInMemoryStore(); }\n";

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string registration, string extra = "")
        => MarkerWithoutBehaviorAnalyzerTests.AnalyzeApplicationAsync(Notifications + extra + Program(registration), new UnnamedHandledNotificationAnalyzer());
}

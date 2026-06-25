using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Diagnostics.HealthChecks;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Unit tests for <see cref="CqrsBindingsHealthCheck" /> that drive
///     <see cref="CqrsBindingsHealthCheck.CheckHealthAsync" /> directly against a mocked
///     <see cref="ICqrsDiagnostics" />, asserting the mapping from binding issue severities to
///     <see cref="HealthCheckResult" /> status and data.
/// </summary>
public sealed class CqrsBindingsHealthCheckTests
{
    private sealed class HealthCheckTarget;

    private sealed class OtherHealthCheckTarget;

    private static CqrsRequestBinding Binding(Type requestType, params CqrsBindingIssue[] issues)
        => new(
            RequestType: requestType,
            ResponseType: typeof(object),
            HandlerType: null,
            ContextType: null,
            PipelineExemptions: Array.Empty<Type>(),
            PreHandlers: Array.Empty<CqrsInterceptorBinding>(),
            PostHandlers: Array.Empty<CqrsInterceptorBinding>(),
            Pipeline: Array.Empty<CqrsPipelineBehaviorBinding>(),
            ExemptedPipeline: Array.Empty<CqrsPipelineBehaviorBinding>(),
            Issues: issues);

    private static CqrsBindingsHealthCheck CreateCheck(params CqrsRequestBinding[] bindings)
    {
        var diagnostics = new Mock<ICqrsDiagnostics>(MockBehavior.Strict);
        diagnostics
            .Setup(d => d.DescribeAllRequests())
            .Returns(bindings);
        return new CqrsBindingsHealthCheck(diagnostics.Object);
    }

    private static HealthCheckContext Context()
        => new()
        {
            Registration = new HealthCheckRegistration(
                "cqrsharp.bindings",
                _ => throw new InvalidOperationException("not used"),
                failureStatus: HealthStatus.Unhealthy,
                tags: null)
        };

    [Fact]
    public async Task CheckHealthAsync_returns_Healthy_when_no_bindings_have_issues()
    {
        var check = CreateCheck(
            Binding(typeof(HealthCheckTarget)),
            Binding(typeof(OtherHealthCheckTarget)));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["requestCount"].Should().Be(2);
        result.Data["errorCount"].Should().Be(0);
        result.Data["warningCount"].Should().Be(0);
        result.Data.Should().NotContainKey("errors");
        result.Data.Should().NotContainKey("warnings");
    }

    [Fact]
    public async Task CheckHealthAsync_returns_Healthy_when_bindings_collection_is_empty()
    {
        var check = CreateCheck();

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["requestCount"].Should().Be(0);
        result.Data["errorCount"].Should().Be(0);
        result.Data["warningCount"].Should().Be(0);
        result.Description.Should().Contain("0 request(s)");
    }

    [Fact]
    public async Task CheckHealthAsync_treats_Info_only_issues_as_Healthy()
    {
        var check = CreateCheck(
            Binding(
                typeof(HealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Info, "CQRS_INFO", "informational only")));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["errorCount"].Should().Be(0);
        result.Data["warningCount"].Should().Be(0);
    }

    [Fact]
    public async Task CheckHealthAsync_returns_Unhealthy_when_a_binding_has_an_Error_issue()
    {
        var check = CreateCheck(
            Binding(
                typeof(HealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Error, "CQRS_NO_HANDLER", "no handler registered")));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["errorCount"].Should().Be(1);
        result.Description.Should().Contain("1 error(s)");

        result.Data.Should().ContainKey("errors");
        var errors = result.Data["errors"].Should().BeAssignableTo<string[]>().Subject;
        errors.Should().ContainSingle(e =>
            e.Contains(typeof(HealthCheckTarget).FullName!) &&
            e.Contains("no handler registered"));
    }

    [Fact]
    public async Task CheckHealthAsync_aggregates_multiple_error_messages_for_a_single_binding()
    {
        var check = CreateCheck(
            Binding(
                typeof(HealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Error, "CQRS_NO_HANDLER", "no handler registered"),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Error, "CQRS_NO_CONTEXT", "context factory missing")));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["errorCount"].Should().Be(2);

        var errors = (string[])result.Data["errors"];
        errors.Should().ContainSingle();
        errors[0].Should().Contain(typeof(HealthCheckTarget).FullName!);
        errors[0].Should().Contain("no handler registered");
        errors[0].Should().Contain("context factory missing");
        // Multiple messages for one binding are joined with "; ".
        errors[0].Should().Contain("no handler registered; context factory missing");
    }

    [Fact]
    public async Task CheckHealthAsync_returns_Unhealthy_listing_only_the_faulted_bindings()
    {
        var check = CreateCheck(
            Binding(typeof(HealthCheckTarget)),
            Binding(
                typeof(OtherHealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Error, "CQRS_NO_HANDLER", "no handler registered")));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["requestCount"].Should().Be(2);
        result.Data["errorCount"].Should().Be(1);

        var errors = (string[])result.Data["errors"];
        errors.Should().ContainSingle()
            .Which.Should().Contain(typeof(OtherHealthCheckTarget).FullName!);
        errors.Should().NotContain(e => e.Contains(typeof(HealthCheckTarget).FullName!));
    }

    [Fact]
    public async Task CheckHealthAsync_returns_Degraded_when_a_binding_has_only_Warning_issues()
    {
        var check = CreateCheck(
            Binding(
                typeof(HealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Warning, "CQRS_AMBIGUOUS", "ambiguous registration")));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["errorCount"].Should().Be(0);
        result.Data["warningCount"].Should().Be(1);
        result.Description.Should().Contain("1 warning(s)");

        result.Data.Should().ContainKey("warnings");
        result.Data.Should().NotContainKey("errors");
        var warnings = result.Data["warnings"].Should().BeAssignableTo<string[]>().Subject;
        warnings.Should().ContainSingle(w =>
            w.Contains(typeof(HealthCheckTarget).FullName!) &&
            w.Contains("ambiguous registration"));
    }

    [Fact]
    public async Task CheckHealthAsync_prefers_Unhealthy_over_Degraded_when_both_errors_and_warnings_present()
    {
        var check = CreateCheck(
            Binding(
                typeof(HealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Warning, "CQRS_AMBIGUOUS", "ambiguous registration")),
            Binding(
                typeof(OtherHealthCheckTarget),
                new CqrsBindingIssue(CqrsBindingIssueSeverity.Error, "CQRS_NO_HANDLER", "no handler registered")));

        var result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["errorCount"].Should().Be(1);
        result.Data["warningCount"].Should().Be(1);

        // Error path wins: only the "errors" detail is surfaced, not "warnings".
        result.Data.Should().ContainKey("errors");
        result.Data.Should().NotContainKey("warnings");
    }
}

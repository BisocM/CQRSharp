using System.Diagnostics.CodeAnalysis;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Registries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The application's <see cref="ICqrsDiagnostics" />: every request the merged route table dispatches, each described
///     through its route by the one describer in this scope, and the configuration inspected over those descriptions.
/// </summary>
internal sealed class CqrsDiagnostics(
    ModuleRouteTable table,
    IServiceProvider services,
    IRequestRegistry requests,
    IContextFactoryRegistry contexts) : ICqrsDiagnostics
{
    private readonly RequestBindingDescriber _describer = new(services, requests, contexts);

    public bool TryDescribeRequest(Type requestType, [NotNullWhen(true)] out CqrsRequestBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        binding = table.Routes.TryGetValue(requestType, out var route) ? route.Describe(_describer)
            : table.StreamRoutes.TryGetValue(requestType, out var stream) ? stream.Describe(_describer)
            : null;
        return binding is not null;
    }

    public CqrsRequestBinding DescribeRequest(Type requestType)
        => TryDescribeRequest(requestType, out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Unknown request type '{requestType.FullName}'. No source-generated module routes it: it has no handler in an " +
                "assembly that runs the CQRSharp source generator, or that assembly's module is not registered.");

    public IReadOnlyList<CqrsRequestBinding> DescribeAllRequests()
    {
        var bindings = new CqrsRequestBinding[table.RequestTypes.Length];
        for (var i = 0; i < bindings.Length; i++)
            bindings[i] = DescribeRequest(table.RequestTypes[i]);
        return bindings;
    }

    public IReadOnlyList<CqrsBindingIssue> DescribeConfiguration() => DescribeConfiguration(DescribeAllRequests());

    /// <summary>The configuration issues, over request descriptions the caller already has (the startup validator's).</summary>
    internal IReadOnlyList<CqrsBindingIssue> DescribeConfiguration(IReadOnlyList<CqrsRequestBinding> bindings)
    {
        var outbox = services.GetService<IOptions<OutboxOptions>>()?.Value ?? new OutboxOptions();
        return CqrsConfigurationInspector.Inspect(services, outbox, bindings);
    }
}

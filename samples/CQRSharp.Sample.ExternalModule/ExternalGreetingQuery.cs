using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Query;

namespace CQRSharp.Sample.ExternalModule;

/// <summary>A query declared in a separate assembly, dispatched by the Sample to prove cross-assembly routing under AOT.</summary>
public sealed class ExternalGreetingQuery : QueryBase<string>
{
    public required string Name { get; init; }
}

/// <summary>Internal handler: registered by THIS assembly's generated module even though the Sample cannot name it.</summary>
internal sealed class ExternalGreetingQueryHandler : IQueryHandler<ExternalGreetingQuery, string>
{
    public Task<string> Handle(ExternalGreetingQuery query, CancellationToken cancellationToken)
        => Task.FromResult($"hello from the external module, {query.Name}");
}

using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class ScopeProbeQueryHandler(SampleScopedMarker scope) : IQueryHandler<ScopeProbeQuery, Guid>
{
    public Task<Guid> Handle(ScopeProbeQuery query, CancellationToken cancellationToken) => Task.FromResult(scope.Id);
}

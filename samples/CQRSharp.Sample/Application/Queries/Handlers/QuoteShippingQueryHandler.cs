using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class QuoteShippingQueryHandler(SampleDiagnostics diagnostics) : IQueryHandler<QuoteShippingQuery, decimal>
{
    private const decimal BaseFee = 4.90m;
    private const decimal PerKilogram = 1.25m;

    public Task<decimal> Handle(QuoteShippingQuery query, CancellationToken cancellationToken)
    {
        diagnostics.CountRun(query.IdempotencyKey);
        return Task.FromResult(BaseFee + PerKilogram * query.WeightKg);
    }
}

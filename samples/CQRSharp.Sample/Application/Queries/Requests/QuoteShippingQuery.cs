using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Queries.Requests;

/// <summary>
///     A query can carry an idempotency key as well: a repeated quote under the same key returns the stored price. The
///     result is a value type, whose replay Native AOT compiles separately from a reference type's.
/// </summary>
public sealed class QuoteShippingQuery(string idempotencyKey, decimal weightKg)
    : QueryBase<decimal, SampleRequestContext>, IIdempotentRequest
{
    public string IdempotencyKey { get; } = idempotencyKey;
    public decimal WeightKg { get; } = weightKg;
}

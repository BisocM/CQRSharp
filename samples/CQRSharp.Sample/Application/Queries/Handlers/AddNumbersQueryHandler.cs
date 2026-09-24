using CQRSharp.Sample.Application.Queries.Requests;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class AddNumbersQueryHandler : IQueryHandler<AddNumbersQuery, int>
{
    public Task<int> Handle(AddNumbersQuery query, CancellationToken cancellationToken)
        => Task.FromResult(query.Left + query.Right);
}

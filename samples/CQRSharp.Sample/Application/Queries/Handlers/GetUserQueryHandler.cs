using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Infrastructure.Persistence;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class GetUserQueryHandler(CustomInMemoryUserStore userStore) : IQueryHandler<GetUserQuery, User?>
{
    public Task<User?> Handle(GetUserQuery query, CancellationToken cancellationToken)
        => Task.FromResult(userStore.GetUserById(query.Id));
}

using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Sample.Data;
using CQRSharp.Sample.Queries.Types;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Queries.Handlers;

public class GetUserQueryHandler(CustomInMemoryUserStore userStore, ILogger<GetUserQueryHandler> logger) 
    : IQueryHandler<GetUserQuery, User?>
{
    public Task<User?> Handle(GetUserQuery query, CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling GetUserQuery for ID: {Id}", query.Id);
        var user = userStore.GetUserById(query.Id);
        return Task.FromResult(user);
    }
}
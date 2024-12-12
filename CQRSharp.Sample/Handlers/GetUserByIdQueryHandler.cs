using CQRSharp.Interfaces.Handlers;
using CQRSharp.Sample.Commands;
using CQRSharp.Sample.Models;

namespace CQRSharp.Sample.Handlers;

public class GetUserByIdQueryHandler : IQueryHandler<GetUserByIdQuery, User?>
{
    private readonly InMemoryUserRepository _repo;

    public GetUserByIdQueryHandler(InMemoryUserRepository repo)
    {
        _repo = repo;
    }

    public Task<User?> Handle(GetUserByIdQuery command, CancellationToken cancellationToken)
    {
        var user = _repo.GetUser(command.UserId);
        return Task.FromResult(user);
    }
}
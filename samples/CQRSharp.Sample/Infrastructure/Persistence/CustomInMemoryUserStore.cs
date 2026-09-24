using System.Collections.Concurrent;
using CQRSharp.Sample.Domain.Entities;

namespace CQRSharp.Sample.Infrastructure.Persistence;

public sealed class CustomInMemoryUserStore
{
    private readonly ConcurrentDictionary<Guid, User> _users = new();

    public void AddUser(User user) => _users.TryAdd(user.Id, user);

    public User? GetUserById(Guid id) => _users.GetValueOrDefault(id);
}

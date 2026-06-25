using System.Collections.Concurrent;
using CQRSharp.Sample.Domain.Entities;

namespace CQRSharp.Sample.Infrastructure.Persistence;

public class CustomInMemoryUserStore
{
    private readonly ConcurrentDictionary<Guid, User> _userStore = new();

    public void AddUser(User user)
    {
        _userStore.TryAdd(user.Id, user);
    }

    public User? GetUserById(Guid id)
    {
        _userStore.TryGetValue(id, out var user);
        return user;
    }
}
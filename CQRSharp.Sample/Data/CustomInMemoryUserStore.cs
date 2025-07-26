using System.Collections.Concurrent;

namespace CQRSharp.Sample.Data;

public record User(string Name, Guid Id);

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
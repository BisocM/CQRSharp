using System;
using System.Collections.Concurrent;

namespace CQRSharp.Sample.Models
{
    public class InMemoryUserRepository
    {
        private readonly ConcurrentDictionary<Guid, User> _users = new();

        public void AddUser(User user)
        {
            _users[user.Id] = user;
        }

        public User? GetUser(Guid id)
        {
            _users.TryGetValue(id, out var user);
            return user;
        }
    }
}
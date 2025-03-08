using System;
using System.Collections.Generic;
using System.Linq;

namespace CQRSharp.Sample.Data;

/// <summary>
/// Represents a custom in-memory store for managing user data.
/// </summary>
public class CustomInMemoryUserStore
{
    public class User(string name, string userId)
    {
        public string Name { get; set; } = name;
        public string UserGuid { get; set; } = userId;
    }

    private readonly List<User> _userStore = [];

    public CustomInMemoryUserStore()
    {
        _userStore.Add(new User("Papa", Guid.NewGuid().ToString()));
        _userStore.Add(new User("Baba", Guid.NewGuid().ToString()));
        _userStore.Add(new User("Mama", Guid.NewGuid().ToString()));
    }

    /// <summary>
    /// Retrieves the list of stored users from the in-memory user store.
    /// </summary>
    /// <returns>A list of users contained within the in-memory store.</returns>
    public List<User> GetUsers() => _userStore;


    /// <summary>
    /// Retrieves a single user with the specified name from the in-memory user store.
    /// </summary>
    /// <param name="name">The name of the user to retrieve.</param>
    /// <returns>The user that matches the specified name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no user with the specified name is found in the store.</exception>
    public User GetUser(string name) => _userStore.FirstOrDefault(x => x.Name == name) ?? throw new InvalidOperationException();

    /// <summary>
    /// Deletes a user with the specified name from the in-memory user store.
    /// </summary>
    /// <param name="name">The name of the user to delete.</param>
    /// <exception cref="InvalidOperationException">Thrown when no user with the specified name is found in the store.</exception>
    public void DeleteUser(User userData) => _userStore.Remove(userData);
}
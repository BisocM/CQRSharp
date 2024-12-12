namespace CQRSharp.Sample.Models;

public class User
{
    public Guid Id { get; set; }
    public string UserName { get; set; }
    public string PasswordHash { get; set; }
}
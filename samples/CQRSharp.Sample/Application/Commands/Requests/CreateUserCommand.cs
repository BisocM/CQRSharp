using System.Data;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public sealed class CreateUserCommand(string name, Guid id) : CommandBase<SampleRequestContext>, ITransactionalCommand
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
}

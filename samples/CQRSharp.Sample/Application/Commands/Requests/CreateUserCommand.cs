using System.Data;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public class CreateUserCommand(string name, Guid id) : CommandBase<SampleRequestContext>, ITransactionalCommand
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.ReadCommitted;
}
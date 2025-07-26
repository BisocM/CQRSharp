using System.Data;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Commands.Types;

public class CreateUserCommand(string name, Guid id) : CommandBase<SampleRequestContext>, ITransactionalRequest
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.ReadCommitted;
}
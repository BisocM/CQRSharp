using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Data;

namespace CQRSharp.Sample.Queries.Types;

public class GetUserQuery(Guid id) : QueryBase<User?, SampleRequestContext>
{
    public Guid Id { get; } = id;
}
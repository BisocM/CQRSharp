using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Domain.Entities;

namespace CQRSharp.Sample.Application.Queries.Requests;

public class GetUserQuery(Guid id) : QueryBase<User?, SampleRequestContext>
{
    public Guid Id { get; } = id;
}
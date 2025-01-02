using CQRSharp.Interfaces.Markers.Query;
using CQRSharp.Sample.Models;

namespace CQRSharp.Sample.Commands;

public class GetUserByIdQuery : QueryBase<User?>
{
    public Guid UserId { get; set; }
}
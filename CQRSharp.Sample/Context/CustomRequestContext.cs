using CQRSharp.Interfaces.Context;

namespace CQRSharp.Sample.Context
{
    public class CustomRequestContext(string requestId, string userId, string userRole, string sourceIp)
        : RequestContextBase(requestId, userId)
    {
        public string? RequestId { get; } = requestId;
        public string? UserId { get; } = userId;

        //Additional custom fields
        public string UserRole { get; } = userRole;
        public string SourceIp { get; } = sourceIp;
    }
}
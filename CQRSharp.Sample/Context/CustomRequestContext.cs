using CQRSharp.Data.Context;

namespace CQRSharp.Sample.Context
{
    public class CustomRequestContext(string requestId, string userId, string userRole, string sourceIp)
        : IRequestContext
    {
        public string? RequestId { get; } = requestId;
        public string? UserId { get; } = userId;

        //Additional custom fields
        public string UserRole { get; } = userRole;
        public string SourceIp { get; } = sourceIp;
    }
}
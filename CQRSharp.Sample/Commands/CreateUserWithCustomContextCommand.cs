using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Commands
{
    public class CreateUserWithCustomContextCommand : CommandBase<CustomRequestContext>
    {
        public string UserName { get; set; }

        [SensitiveData]
        public string Password { get; set; }

        public Guid CreatedUserId { get; set; }
    }
}
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Command;

namespace CQRSharp.Sample.Commands.Types;

[MenuOrientation(MenuState.SecondaryMenu, 2)]
public class DeleteUserCommand : CommandBase<SampleRequestContext>
{
}
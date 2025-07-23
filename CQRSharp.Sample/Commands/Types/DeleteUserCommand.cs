using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Commands.Types;

[MenuOrientation(MenuState.SecondaryMenu, 2)]
public class DeleteUserCommand : CommandBase<SampleRequestContext>
{
}
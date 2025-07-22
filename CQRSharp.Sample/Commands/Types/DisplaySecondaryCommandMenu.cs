using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;

namespace CQRSharp.Sample.Commands.Types;

[MenuOrientation(MenuState.PrimaryMenu, 2)]
public class DisplaySecondaryCommandMenu : CommandBase<SampleRequestContext>
{
}
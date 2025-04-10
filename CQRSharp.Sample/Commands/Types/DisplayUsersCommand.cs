using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;

namespace CQRSharp.Sample.Commands.Types;

[MenuOrientation(MenuState.SecondaryMenu, 1)]
public class DisplayUsersCommand : CommandBase<SampleRequestContext>
{
    //The command data must remain empty due to the nature of our application having a generic command pattern.
}
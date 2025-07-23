using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Commands.Types;

[MenuOrientation(MenuState.SecondaryMenu, 1)]
public class DisplayUsersCommand : CommandBase<SampleRequestContext>
{
    //The command data must remain empty due to the nature of our application having a generic command pattern.
}
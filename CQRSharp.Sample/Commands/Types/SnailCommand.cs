using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Commands.Types;

//Position this command in the secondary menu.
[MenuOrientation(MenuState.SecondaryMenu, 1)]
public class SnailCommand : CommandBase<SampleRequestContext> { }
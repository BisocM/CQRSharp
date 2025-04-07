using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Commands.Types;

[MenuOrientation(MenuState.PrimaryMenu, 2)]
public class DisplaySecondaryCommandMenu : CommandBase<SampleRequestContext> { }
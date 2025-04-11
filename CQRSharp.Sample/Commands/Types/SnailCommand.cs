using CQRSharp.Sample.Attributes;
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;

namespace CQRSharp.Sample.Commands.Types;

//Position this command in the secondary menu.
[CustomInterceptor(10)]
[MenuOrientation(MenuState.PrimaryMenu, 1)]
public class SnailCommand : CommandBase<SampleRequestContext>;
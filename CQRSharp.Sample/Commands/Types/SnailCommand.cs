using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Data;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Shared.Core.Data.Interfaces.Handlers;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Command;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Query;

namespace CQRSharp.Sample.Commands.Types;

//Position this command in the secondary menu.
[MenuOrientation(MenuState.PrimaryMenu, 1)]
public class SnailCommand : CommandBase<SampleRequestContext>;
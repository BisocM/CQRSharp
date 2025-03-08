using System;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Attributes.Menu;

/// <summary>
/// An attribute that sets the orientation of the specified command within the menu.
/// </summary>
/// <param name="menuId">The MenuState ID of the menu that the command belongs to.</param>
/// <param name="priority">The priority in which the command should be displayed. Lower number - higher priority.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class MenuOrientationAttribute(MenuState menuId, int priority = 0) : Attribute
{
    public MenuState MenuId { get; } = menuId;
    public int Priority { get; } = priority;
}
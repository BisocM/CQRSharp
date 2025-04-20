using System.Reflection;
using System.Text.RegularExpressions;
using CQRSharp.Core.Requests;
using CQRSharp.Sample.Attributes.Menu;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Cancellation;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Management.Menu;

/// <summary>
///     Manages the menu system for the Chroma application, including displaying menus and executing commands.
/// </summary>
public class MenuManager
{
    private readonly CancellationManager _cancellationManager;
    private readonly IRequestDispatcher _requestDispatcher;

    private readonly ILogger<MenuManager> _logger;

    /// <summary>
    ///     A dictionary mapping each <see cref="MenuState" /> to a list of commands and their priorities.
    /// </summary>
    private readonly Dictionary<MenuState, List<(Type commandType, int priority)>> _menus = new();

    private readonly MenuStateMachine _stateMachine;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MenuManager" /> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider for dependency injection.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cancellationManager">The cancellation manager.</param>
    public MenuManager(IServiceProvider serviceProvider, ILogger<MenuManager> logger,
        CancellationManager cancellationManager)
    {
        _logger = logger;
        _stateMachine = new MenuStateMachine(MenuState.PrimaryMenu);
        _cancellationManager = cancellationManager;
        _requestDispatcher = serviceProvider.GetRequiredService<IRequestDispatcher>();

        SetupMenus();
        _cancellationManager.Initialize();
    }

    /// <summary>
    ///     Discovers and registers commands for each menu based on the <see cref="MenuOrientationAttribute" />.
    /// </summary>
    private void SetupMenus()
    {
        var commandTypes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.GetCustomAttribute<MenuOrientationAttribute>() != null);

        foreach (var type in commandTypes)
        {
            var attribute = type.GetCustomAttribute<MenuOrientationAttribute>();
            if (attribute == null) continue;

            if (!_menus.TryGetValue(attribute.MenuId, out var list))
            {
                list = new List<(Type, int)>();
                _menus[attribute.MenuId] = list;
            }

            list.Add((type, attribute.Priority));
            _logger.LogDebug(
                $"Registered command {type.Name} with priority {attribute.Priority} in menu {attribute.MenuId}.");
        }

        _logger.LogInformation(
            $"Menu setup complete with {_menus.Count} menus and {_menus.Sum(x => x.Value.Count)} commands.");
    }

    public async Task ShowMenuAsync(MenuState menuState)
    {
        _logger.LogInformation($"Displaying the menu with state: {menuState}.");
        _stateMachine.TransitionTo(menuState);

        //Clear the console to display the menu.
        Console.Clear();

        //Start the while loop. This is required since the CQRS is set to sync mode.
        while (true)
        {
            //Retrieve the current state. If the menu state that we are trying to switch to is not found,
            //then we will break out of the loop.
            //If present, we retrieve menuItems, which is a list of commands for that specific menu.
            var menuId = _stateMachine.CurrentState;
            if (!_menus.TryGetValue(menuId, out List<(Type commandType, int priority)>? menuItems))
            {
                _logger.LogWarning($"Menu {menuId} not found.");
                break;
            }

            //Arrange all the menuItems in terms of their priority.
            var sortedMenuItems = menuItems.OrderBy(it => it.priority).ToList();

            //Now, we can retrieve the user selection.
            var userSelection = RetrieveUserSelection(sortedMenuItems);
            _logger.LogInformation($"User selected command {userSelection.commandType.Name} in menu {menuId}.");

            //Attempt to execute the command here!
            try
            {
                //Create an instance of the selected command.
                var selectedCommandType = userSelection.commandType;
                if (Activator.CreateInstance(selectedCommandType) is CommandBase<SampleRequestContext> command)
                {
                    //Reset the cancellation token to make sure that the command does not get cancelled.
                    _cancellationManager.ResetCancellation();

                    //Execute the command. Add the logging statement BEFORE the dispatcher call, since it is a blocking call.
                    //So saying "sent to dispatcher" after command executed already makes no sense!
                    _logger.LogInformation("Command successfully sent to the dispatcher.");
                    var result = await _requestDispatcher.ExecuteCommand(command, _cancellationManager.Token);

                    //TODO: Bit of an annoying way of doing this, so maybe clean this up?
                    //Console.Clear();

                    if (result is { IsSuccess: false })
                    {
                        _logger.LogError(result.ErrorMessage,
                            $"Command {selectedCommandType.Name} failed with error: {result.ErrorMessage}. Error Code: {result.ErrorCode}.");
                        Console.WriteLine($"[red]{result.ErrorMessage}[/]");
                    }

                    //Transition to the next menu state based on the result.
                    _logger.LogInformation(
                        $"Transitioning to the next menu state - {command.Context.NextState ?? _stateMachine.CurrentState}.");
                    _stateMachine.TransitionTo(command.Context.NextState ?? _stateMachine.CurrentState);
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogInformation($"Command {userSelection.commandType.Name} was cancelled by user.");
                Console.WriteLine("Command execution cancelled by user.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"An error occurred while executing command {userSelection.commandType.Name}.");
                Console.WriteLine($"An error occurred: {e.Message}.");
            }
        }
    }

    private (Type commandType, int priorityvoid) RetrieveUserSelection(
        List<(Type commandType, int priority)>? sortedMenuItems)
    {
        var menuDisplay = sortedMenuItems.Aggregate("",
            (current, menuItem) => current + $"{menuItem.priority}. {FormatCommandName(menuItem.commandType.Name)}\n");

        //Print out the menu and query the user on what they want via ReadLine. Verify the input to be an int, and continue.
        Console.WriteLine(menuDisplay);
        var userSelection = Console.ReadLine();

        int.TryParse(userSelection, out var selection);

        //Return the selected item.
        return sortedMenuItems[selection - 1];
    }

    /// <summary>
    ///     Formats a command class name into a user-friendly name for display.
    /// </summary>
    /// <param name="className">The class name of the command.</param>
    /// <returns>A formatted command name.</returns>
    private string FormatCommandName(string className)
    {
        //Remove the "Command" suffix.
        if (className.EndsWith("Command"))
            className = className[..^"Command".Length];

        //Split the remaining string into words based on uppercase letters.
        var words = Regex
            .Matches(className, @"[A-Z][a-z]*")
            .Select(match => match.Value);

        return string.Join(" ", words);
    }
}
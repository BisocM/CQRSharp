using CQRSharp.Core.Requests;
using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Management.Menu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Services;

/// <summary>
///     A hosted service that runs upon application startup and demonstrates
///     various capabilities of the CQRSharp library by executing commands and queries.
/// </summary>
public class SampleHostedService(IServiceProvider services, ILogger<SampleHostedService> logger)
    : BackgroundService
{
    private readonly ILogger<SampleHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // AoT Debugging - publish the app, then run this. This is because MenuManager does not support AOT.
        SnailCommand command = new();
        await Task.Delay(1000);
        var dispatcher = services.GetRequiredService<IRequestDispatcher>();
        await dispatcher.ExecuteAsync(command, stoppingToken);
        Console.Read();

        //The only responsibility of this hosted service is to run startup operations and then display our menu.
        //The menu is a separate class that is not part of the CQRSharp library.
        var menuManager = services.GetRequiredService<MenuManager>();

        //Display the menu.
        await menuManager.ShowMenuAsync(MenuState.PrimaryMenu);
    }
}
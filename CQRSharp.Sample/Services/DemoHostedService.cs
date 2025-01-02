using CQRSharp.Core.Dispatch;
using CQRSharp.Sample.Commands;
using CQRSharp.Sample.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Services;

/// <summary>
///     A hosted service that runs upon application startup and demonstrates
///     various capabilities of the CQRSharp library by executing commands and queries.
/// </summary>
public class DemoHostedService : BackgroundService
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<DemoHostedService> _logger;
    private readonly InMemoryUserRepository _repo;

    public DemoHostedService(IDispatcher dispatcher, ILogger<DemoHostedService> logger, InMemoryUserRepository repo)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _repo = repo;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DemoHostedService started. Demonstrating capabilities of CQRSharp...");

        // 1. Create a new user (Command)
        var createUserCommand = new CreateUserCommand
        {
            UserName = "JohnDoe",
            Password = "SuperSecretPassword123!"
        };
        await _dispatcher.ExecuteCommand(createUserCommand, stoppingToken);
        _logger.LogInformation("CreateUserCommand executed.");
        
        //Same thing but with custom context.
        var customContextCommand = new CreateUserWithCustomContextCommand
        {
            UserName = "JaneDoe",
            Password = "AnotherSecretPass!"
        };

        // Execute this command. The CustomContextLoggingBehavior (if registered) will log the custom context fields.
        await _dispatcher.ExecuteCommand(customContextCommand, stoppingToken);
        _logger.LogInformation("CreateUserWithCustomContextCommand executed with custom context.");

        // 2. Query the user by ID (Query)
        var getUserQuery = new GetUserByIdQuery { UserId = createUserCommand.CreatedUserId };
        var user = await _dispatcher.ExecuteQuery(getUserQuery, stoppingToken);
        _logger.LogInformation($"GetUserByIdQuery executed. Queried user: {user?.UserName}");

        // 3. Update the user password (Command with sensitive data)
        var updatePasswordCommand = new UpdateUserPasswordCommand
        {
            UserId = createUserCommand.CreatedUserId,
            NewPassword = "AnotherSecret#456!"
        };
        await _dispatcher.ExecuteCommand(updatePasswordCommand, stoppingToken);
        _logger.LogInformation("UpdateUserPasswordCommand executed.");

        // 4. Test rate limiting by making multiple queries in quick succession
        _logger.LogInformation("Testing rate limiting by querying multiple times...");
        for (var i = 0; i < 10; i++)
            try
            {
                var result = await _dispatcher.ExecuteQuery(getUserQuery, stoppingToken);
                _logger.LogInformation($"Rate limit test #{i + 1}: User {result?.UserName} retrieved successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Rate limit test #{i + 1}: {ex.Message}");
            }

        _logger.LogInformation("All demonstrations complete. DemoHostedService is idle now.");
    }
}
using CQRSharp.Core.Pipelines;
using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Data;
using CQRSharp.Sample.Queries.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Services;

public class SampleHostedService(
    IRequestDispatcher dispatcher,
    ILogger<SampleHostedService> logger,
    IHostApplicationLifetime appLifetime)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("--- CQRSharp Feature Demonstration ---");

        // 1. Transactional Command with Outbox Notification
        logger.LogInformation("\n[1] Demonstrating a transactional command that creates a user and publishes an outbox notification...");
        var userId = Guid.NewGuid();
        var createUserCommand = new CreateUserCommand("BisocM", userId);
        await dispatcher.ExecuteAsync(createUserCommand, stoppingToken);
        logger.LogInformation("CreateUserCommand executed. The UserCreatedNotification is now in the outbox store, waiting for the OutboxProcessor.");

        // Allow time for the OutboxProcessor to run
        logger.LogInformation("...waiting for OutboxProcessor to dispatch notification...");
        await Task.Delay(3000, stoppingToken);

        // 2. Simple Query
        logger.LogInformation("\n[2] Demonstrating a simple query to fetch the user we just created...");
        var userQuery = new GetUserQuery(userId);
        var user = await dispatcher.ExecuteAsync(userQuery, stoppingToken) as User;
        logger.LogInformation("GetUserQuery returned: User(Name={Name}, Id={Id})", user?.Name, user?.Id);

        // 3. Sensitive Data Redaction in Custom Pipeline
        logger.LogInformation("\n[3] Demonstrating sensitive data redaction via a custom logging pipeline...");
        var updatePasswordCommand = new UpdateUserPasswordCommand(userId, "MySuperSecretPassword123!");
        await dispatcher.ExecuteAsync(updatePasswordCommand, stoppingToken);
        logger.LogInformation("Check the logs above for '[LoggingPipeline] Handling request UpdateUserPasswordCommand'. The 'NewPassword' property should be redacted.");

        // 4. Pipeline Exemption
        logger.LogInformation("\n[4] Demonstrating pipeline behavior exemption...");
        var pingCommand = new PingCommand();
        await dispatcher.ExecuteAsync(pingCommand, stoppingToken);
        logger.LogInformation("PingCommand was executed. It has [PipelineExemption(typeof(LoggingPipelineBehavior))] so it was NOT logged by our custom pipeline.");
        
        // 5. Attribute-based Interceptor
        logger.LogInformation("\n[5] Demonstrating attribute-based interceptors (IPreHandler/IPostHandler)...");
        await dispatcher.ExecuteAsync(new InterceptorDemoCommand(), stoppingToken);
        logger.LogInformation("Check the logs for PRE-HANDLER and POST-HANDLER messages from CustomInterceptorAttribute.");

        // 6. Rate Limiting
        logger.LogInformation("\n[6] Demonstrating rate limiting (2 requests per second). Expecting failures...");
        for (int i = 0; i < 3; i++)
        {
            try
            {
                logger.LogInformation("Dispatching PingCommand, attempt {Attempt}...", i + 1);
                await dispatcher.ExecuteAsync(new PingCommand(), stoppingToken);
                logger.LogInformation("Attempt {Attempt} succeeded.", i + 1);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Attempt {Attempt} failed as expected: {Message}", i + 1, ex.Message);
            }
        }
        await Task.Delay(1500, stoppingToken); // Wait for tokens to replenish
        logger.LogInformation("...waited for tokens to replenish. Next request should succeed.");
        await dispatcher.ExecuteAsync(new PingCommand(), stoppingToken);
        logger.LogInformation("Final attempt succeeded.");

        // 7. Resilience (Retries)
        logger.LogInformation("\n[7] Demonstrating resilience. This command will fail twice then succeed...");
        try
        {
            await dispatcher.ExecuteAsync(new FailingCommand(), stoppingToken);
            logger.LogInformation("FailingCommand succeeded after retries.");
        }
        catch (Exception ex)
        {
            logger.LogError("FailingCommand failed after all retries: {Message}", ex.Message);
        }

        // 8. Timeout
        logger.LogInformation("\n[8] Demonstrating request timeout (2 seconds)...");
        try
        {
            await dispatcher.ExecuteAsync(new SlowCommand(), stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("SlowCommand failed as expected: {Message}", ex.Message);
        }

        logger.LogInformation("\n--- Demonstration Complete ---");
        appLifetime.StopApplication();
    }
}
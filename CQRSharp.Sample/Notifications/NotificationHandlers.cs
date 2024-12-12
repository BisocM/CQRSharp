using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Interfaces.Notifications;
using CQRSharp.Sample.Commands;
using CQRSharp.Sample.Models;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Notifications
{
    public class UserCommandInitiatedHandler : INotificationHandler<CommandInitiatedNotification>
    {
        private readonly ILogger<UserCommandInitiatedHandler> _logger;
        public UserCommandInitiatedHandler(ILogger<UserCommandInitiatedHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(CommandInitiatedNotification notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[Notification] Command {notification.CommandName} initiated.");
            return Task.CompletedTask;
        }
    }

    public class UserCommandCompletedHandler : INotificationHandler<CommandCompletedNotification>
    {
        private readonly ILogger<UserCommandCompletedHandler> _logger;

        public UserCommandCompletedHandler(ILogger<UserCommandCompletedHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[Notification] Command {notification.CommandName} completed with result: {notification.Result}");
            return Task.CompletedTask;
        }
    }

    public class UserQueryInitiatedHandler : INotificationHandler<QueryInitiatedNotification<User?>>
    {
        private readonly ILogger<UserQueryInitiatedHandler> _logger;

        public UserQueryInitiatedHandler(ILogger<UserQueryInitiatedHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(QueryInitiatedNotification<User?> notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[Notification] Query {notification.QueryName} initiated.");
            return Task.CompletedTask;
        }
    }

    public class UserQueryCompletedHandler : INotificationHandler<QueryCompletedNotification<User?>>
    {
        private readonly ILogger<UserQueryCompletedHandler> _logger;

        public UserQueryCompletedHandler(ILogger<UserQueryCompletedHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(QueryCompletedNotification<User?> notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[Notification] Query {notification.QueryName} completed with result: {notification.Result}");
            return Task.CompletedTask;
        }
    }
}
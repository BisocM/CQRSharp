using System.Collections.Concurrent;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Helpers;
using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Interfaces.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CQRSharp.Tests
{
    public class GeneralTests
    {
        /***********************************************************************
         *  1) CommandSanitizer Tests
         ***********************************************************************/
        [Fact]
        public void Sanitize_ReturnsEmptyStringIfExecutionContextLoggingDisabled()
        {
            //Arrange
            var request = new MockRequest { NormalField = "TestValue" };
            var options = new DispatcherOptions
            {
                EnableExecutionContextLogging = false,
                EnableSensitiveDataLogging = false
            };

            //Act
            var result = CommandSanitizer.Sanitize(request, options);

            //Assert
            result.Should().BeEmpty("logging is disabled entirely, so no serialization should occur.");
        }

        [Fact]
        public void Sanitize_RedactsSensitiveData_WhenNotAllowedToLog()
        {
            //Arrange
            var request = new MockRequest
            {
                NormalField = "TestValue",
                SensitiveField = "TopSecret"
            };
            var options = new DispatcherOptions
            {
                EnableExecutionContextLogging = true,
                EnableSensitiveDataLogging = false
            };

            //Act
            var result = CommandSanitizer.Sanitize(request, options);

            //Assert
            result.Should().Contain("\"SensitiveField\": \"***REDACTED***\"")
                .And.Contain("\"NormalField\": \"TestValue\"");
        }

        [Fact]
        public void Sanitize_LeavesSensitiveData_WhenLoggingIsAllowed()
        {
            //Arrange
            var request = new MockRequest
            {
                NormalField = "TestValue",
                SensitiveField = "TopSecret"
            };
            var options = new DispatcherOptions
            {
                EnableExecutionContextLogging = true,
                EnableSensitiveDataLogging = true
            };

            //Act
            var result = CommandSanitizer.Sanitize(request, options);

            //Assert
            result.Should().Contain("\"SensitiveField\": \"TopSecret\"");
        }

        /***********************************************************************
         *  2) BackgroundTaskQueue Tests
         ***********************************************************************/
        [Fact]
        public async Task BackgroundTaskQueue_CanQueueAndDequeueSuccessfully()
        {
            //Arrange
            var queue = new BackgroundTaskQueue(capacity: 2);
            Func<CancellationToken, Task> workItem = _ => Task.CompletedTask;

            //Act
            queue.QueueBackgroundWorkItem(workItem);
            var dequeuedItem = await queue.DequeueAsync(CancellationToken.None);

            //Assert
            dequeuedItem.Should().NotBeNull();
            dequeuedItem.Should().BeSameAs(workItem);
        }

        [Fact]
        public void BackgroundTaskQueue_ThrowsExceptionWhenWorkItemIsNull()
        {
            //Arrange
            var queue = new BackgroundTaskQueue();

            //Act
            Action act = () => queue.QueueBackgroundWorkItem(null);

            //Assert
            act.Should().Throw<ArgumentNullException>();
        }

        /***********************************************************************
         *  3) NotificationDispatcher Tests
         ***********************************************************************/
        [Fact]
        public async Task NotificationDispatcher_PublishesToAllHandlers()
        {
            //Arrange: create two mock notification handlers
            var handler1 = new Mock<INotificationHandler<SampleNotification>>();
            var handler2 = new Mock<INotificationHandler<SampleNotification>>();

            var services = new ServiceCollection();
            services.AddSingleton<INotificationHandler<SampleNotification>>(handler1.Object);
            services.AddSingleton<INotificationHandler<SampleNotification>>(handler2.Object);
            services.AddSingleton<NotificationDispatcher>();
            var provider = services.BuildServiceProvider();

            var dispatcher = provider.GetRequiredService<NotificationDispatcher>();
            var notification = new SampleNotification();

            //Act
            await dispatcher.Publish(notification);

            //Assert
            handler1.Verify(h => h.Handle(notification, It.IsAny<CancellationToken>()), Times.Once);
            handler2.Verify(h => h.Handle(notification, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task NotificationDispatcher_GracefullyHandlesNoHandlers()
        {
            //Arrange
            var services = new ServiceCollection();
            //No handlers for SampleNotification at all
            services.AddSingleton<NotificationDispatcher>();
            var provider = services.BuildServiceProvider();

            var dispatcher = provider.GetRequiredService<NotificationDispatcher>();
            var notification = new SampleNotification();

            //Act & Assert: Should not throw an error even if no handlers
            await dispatcher.Publish(notification);
        }

        /***********************************************************************
         *  4) HandlerRegistry Tests
         ***********************************************************************/
        [Fact]
        public void HandlerRegistry_ReturnsNull_WhenNotRegistered()
        {
            //Arrange
            var handlerDict = new ConcurrentDictionary<Type, Type>();
            var registry = new HandlerRegistry(handlerDict);

            //Act
            var result = registry.GetHandlerType(typeof(UnregisteredRequest));

            //Assert
            result.Should().BeNull();
        }

        [Fact]
        public void HandlerRegistry_ReturnsCorrectHandler()
        {
            //Arrange
            var handlerDict = new ConcurrentDictionary<Type, Type>();
            handlerDict.TryAdd(typeof(RegisteredRequest), typeof(RegisteredRequestHandler));
            var registry = new HandlerRegistry(handlerDict);

            //Act
            var result = registry.GetHandlerType(typeof(RegisteredRequest));

            //Assert
            result.Should().Be(typeof(RegisteredRequestHandler));
        }

        /***********************************************************************
         *  Auxiliary types used in these tests
         ***********************************************************************/

        private class MockRequest : IRequest
        {
            public IRequestContext? Context { get; set; }

            public string NormalField { get; set; } = "Default";
            
            [SensitiveData]
            public string SensitiveField { get; set; } = "Sensitive";

            //Could have other members or methods as needed
        }

        private class UnregisteredRequest : IRequest
        {
            public IRequestContext? Context { get; set; }
        }

        private class RegisteredRequest : IRequest
        {
            public IRequestContext? Context { get; set; }
        }

        //Dummy "handler" type
        private class RegisteredRequestHandler
        {
            //Not a real handler signature—just a placeholder
        }

        //Example notification. Keep public!
        public class SampleNotification : INotification { }
    }
}
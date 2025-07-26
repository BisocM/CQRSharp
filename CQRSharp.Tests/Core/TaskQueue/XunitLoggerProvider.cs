// CQRSharp.Tests/Core/TaskQueue/XunitLoggerProvider.cs
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CQRSharp.Tests.Core.TaskQueue;

/// <summary>
/// Provides an <see cref="ILogger"/> that writes log messages to the xUnit <see cref="ITestOutputHelper"/>.
/// </summary>
public class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _testOutputHelper;

    public XunitLoggerProvider(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new XunitLogger(_testOutputHelper, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private class XunitLogger : ILogger
    {
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly string _categoryName;

        public XunitLogger(ITestOutputHelper testOutputHelper, string categoryName)
        {
            _testOutputHelper = testOutputHelper;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                _testOutputHelper.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {_categoryName}: {formatter(state, exception)}");
                if (exception != null)
                {
                    _testOutputHelper.WriteLine(exception.ToString());
                }
            }
            catch (Exception)
            {
                // Ignored. Can happen if test output is disposed.
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public static NoopScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
using NimBus.Core.Logging;
using NimBus.Core.Messages;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// Simple ILoggerProvider and ILogger for test use.
/// </summary>
internal sealed class TestLoggerProvider : ILoggerProvider
{
    private readonly TestLogger _logger = new();

    public ILogger GetContextualLogger(IMessageContext messageContext) => _logger;
    public ILogger GetContextualLogger(IMessage message) => _logger;
    public ILogger GetContextualLogger(string correlationId) => _logger;
}

internal sealed class TestLogger : ILogger
{
    public void Verbose(string messageTemplate, params object[] propertyValues) { }
    public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) { }
    public void Information(string messageTemplate, params object[] propertyValues) { }
    public void Information(Exception exception, string messageTemplate, params object[] propertyValues) { }
    public void Error(string messageTemplate, params object[] propertyValues) { }
    public void Error(Exception exception, string messageTemplate, params object[] propertyValues) { }
    public void Fatal(string messageTemplate, params object[] propertyValues) { }
    public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) { }
}

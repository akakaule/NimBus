using NimBus.Core.Logging;
using NimBus.Core.Messages;
using System;

namespace NimBus.Testing;

/// <summary>
/// No-op logger provider for test use.
/// Prevents NullReferenceException in MessageHandler.Handle when no real logger is configured.
/// </summary>
internal sealed class NullLoggerProvider : ILoggerProvider
{
    internal static readonly NullLoggerProvider Instance = new();

    public ILogger GetContextualLogger(IMessageContext messageContext) => NullLogger.Instance;
    public ILogger GetContextualLogger(IMessage message) => NullLogger.Instance;
    public ILogger GetContextualLogger(string correlationId) => NullLogger.Instance;
}

internal sealed class NullLogger : ILogger
{
    internal static readonly NullLogger Instance = new();

    public void Verbose(string messageTemplate, params object[] propertyValues) { }
    public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) { }
    public void Information(string messageTemplate, params object[] propertyValues) { }
    public void Information(Exception exception, string messageTemplate, params object[] propertyValues) { }
    public void Error(string messageTemplate, params object[] propertyValues) { }
    public void Error(Exception exception, string messageTemplate, params object[] propertyValues) { }
    public void Fatal(string messageTemplate, params object[] propertyValues) { }
    public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) { }
}

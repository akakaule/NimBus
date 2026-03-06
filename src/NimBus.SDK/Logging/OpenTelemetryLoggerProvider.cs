using System.Collections.Generic;
using NimBus.Core.Logging;
using NimBus.Core.Messages;
using Microsoft.Extensions.Logging;
using ILogger = NimBus.Core.Logging.ILogger;
using ILoggerProvider = NimBus.Core.Logging.ILoggerProvider;

namespace NimBus.SDK.Logging
{
    /// <summary>
    /// Bridges the platform's Core.Logging.ILoggerProvider to Microsoft.Extensions.Logging.ILoggerFactory,
    /// enabling OpenTelemetry export via the standard .NET logging pipeline.
    /// Uses ILogger.BeginScope for contextual properties instead of Serilog's ForContext.
    /// </summary>
    public class OpenTelemetryLoggerProvider : ILoggerProvider
    {
        private readonly ILoggerFactory _loggerFactory;

        public OpenTelemetryLoggerProvider(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        public ILogger GetContextualLogger(IMessageContext messageContext)
        {
            var logger = _loggerFactory.CreateLogger("NimBus.MessageContext");
            var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                [$"DIS.{nameof(IMessageContext.CorrelationId)}"] = messageContext.CorrelationId,
                [$"DIS.{nameof(IMessageContext.EventId)}"] = messageContext.EventId,
                [$"DIS.{nameof(IMessageContext.From)}"] = messageContext.From,
                [$"DIS.{nameof(IMessageContext.IsDeferred)}"] = messageContext.IsDeferred,
                [$"DIS.{nameof(IMessageContext.MessageId)}"] = messageContext.MessageId,
                [$"DIS.{nameof(IMessageContext.MessageType)}"] = messageContext.MessageType,
                [$"DIS.{nameof(IMessageContext.SessionId)}"] = messageContext.SessionId,
                [$"DIS.{nameof(IMessageContext.To)}"] = messageContext.To,
                [$"DIS.{nameof(EventContent.EventTypeId)}"] = messageContext.MessageContent?.EventContent?.EventTypeId,
                [$"DIS.{nameof(EventContent.EventJson)}"] = messageContext.MessageContent?.EventContent?.EventJson,
            });

            return new ScopedOpenTelemetryLoggerAdapter(logger, scope);
        }

        public ILogger GetContextualLogger(IMessage message)
        {
            var logger = _loggerFactory.CreateLogger("NimBus.Message");
            var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                [$"DIS.{nameof(IMessage.CorrelationId)}"] = message.CorrelationId,
                [$"DIS.{nameof(IMessage.EventId)}"] = message.EventId,
                [$"DIS.{nameof(IMessage.MessageType)}"] = message.MessageType,
                [$"DIS.{nameof(IMessage.SessionId)}"] = message.SessionId,
                [$"DIS.{nameof(IMessage.To)}"] = message.To,
                [$"DIS.{nameof(EventContent.EventTypeId)}"] = message.MessageContent?.EventContent?.EventTypeId,
                [$"DIS.{nameof(EventContent.EventJson)}"] = message.MessageContent?.EventContent?.EventJson,
            });

            return new ScopedOpenTelemetryLoggerAdapter(logger, scope);
        }

        public ILogger GetContextualLogger(string correlationId)
        {
            var logger = _loggerFactory.CreateLogger("NimBus.Correlation");
            var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                [$"DIS.{nameof(IMessage.CorrelationId)}"] = correlationId,
            });

            return new ScopedOpenTelemetryLoggerAdapter(logger, scope);
        }
    }

    /// <summary>
    /// Logger adapter that holds a scope reference to keep it alive for the lifetime of the logger.
    /// </summary>
    internal class ScopedOpenTelemetryLoggerAdapter : OpenTelemetryLoggerAdapter, System.IDisposable
    {
        private readonly System.IDisposable? _scope;

        public ScopedOpenTelemetryLoggerAdapter(Microsoft.Extensions.Logging.ILogger logger, System.IDisposable? scope)
            : base(logger)
        {
            _scope = scope;
        }

        public void Dispose()
        {
            _scope?.Dispose();
        }
    }
}

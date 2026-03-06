using NimBus.Core.Logging;
using NimBus.Core.Messages;
using ILogger = NimBus.Core.Logging.ILogger;

namespace NimBus.SDK.Logging
{
    [System.Obsolete("Use OpenTelemetryLoggerProvider instead. This class will be removed in a future version.")]
    public class LoggerProvider : ILoggerProvider
    {
        private readonly Serilog.ILogger _baseLogger;

        public LoggerProvider(Serilog.ILogger baseLogger)
        {
            _baseLogger = baseLogger;
        }

        public Core.Logging.ILogger GetContextualLogger(IMessageContext messageContext)
        {
            var logger =
                _baseLogger
                .ForContext($"DIS.{nameof(IMessageContext.CorrelationId)}", messageContext.CorrelationId)
                .ForContext($"DIS.{nameof(IMessageContext.EventId)}", messageContext.EventId)
                .ForContext($"DIS.{nameof(IMessageContext.From)}", messageContext.From)
                .ForContext($"DIS.{nameof(IMessageContext.IsDeferred)}", messageContext.IsDeferred)
                .ForContext($"DIS.{nameof(IMessageContext.MessageId)}", messageContext.MessageId)
                .ForContext($"DIS.{nameof(IMessageContext.MessageType)}", messageContext.MessageType)
                .ForContext($"DIS.{nameof(IMessageContext.SessionId)}", messageContext.SessionId)
                .ForContext($"DIS.{nameof(IMessageContext.To)}", messageContext.To)
                .ForContext($"DIS.{nameof(EventContent.EventTypeId)}", messageContext.MessageContent?.EventContent?.EventTypeId)
                .ForContext($"DIS.{nameof(EventContent.EventJson)}", messageContext.MessageContent?.EventContent?.EventJson);
            
            return new SerilogAdapter(logger);
        }

        public Core.Logging.ILogger GetContextualLogger(IMessage message)
        {
            var logger =
                _baseLogger
                .ForContext($"DIS.{nameof(IMessage.CorrelationId)}", message.CorrelationId)
                .ForContext($"DIS.{nameof(IMessage.EventId)}", message.EventId)
                .ForContext($"DIS.{nameof(IMessage.MessageType)}", message.MessageType)
                .ForContext($"DIS.{nameof(IMessage.SessionId)}", message.SessionId)
                .ForContext($"DIS.{nameof(IMessage.To)}", message.To)
                .ForContext($"DIS.{nameof(EventContent.EventTypeId)}", message.MessageContent?.EventContent?.EventTypeId)
                .ForContext($"DIS.{nameof(EventContent.EventJson)}", message.MessageContent?.EventContent?.EventJson);

            return new SerilogAdapter(logger);
        }

        public ILogger GetContextualLogger(string correlationId)
        {
            var logger =
                _baseLogger
                    .ForContext($"DIS.{nameof(IMessage.CorrelationId)}", correlationId);

            return new SerilogAdapter(logger);
        }
    }
}

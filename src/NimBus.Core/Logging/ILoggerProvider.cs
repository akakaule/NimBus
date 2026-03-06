using NimBus.Core.Messages;

namespace NimBus.Core.Logging
{
    public interface ILoggerProvider
    {
        ILogger GetContextualLogger(IMessageContext messageContext);

        ILogger GetContextualLogger(IMessage message);
        ILogger GetContextualLogger(string correlationId);
    }
}

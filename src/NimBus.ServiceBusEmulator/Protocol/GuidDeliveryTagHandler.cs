using Amqp.Handler;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class GuidDeliveryTagHandler : IHandler
{
    public bool CanHandle(Amqp.Handler.EventId id) => id is
        Amqp.Handler.EventId.SendDelivery or
        Amqp.Handler.EventId.ConnectionAccept or
        Amqp.Handler.EventId.ConnectionRemoteOpen or
        Amqp.Handler.EventId.LinkRemoteOpen;

    public void Handle(Event protocolEvent)
    {
        EmulatorDiagnostics.Write(protocolEvent.Id.ToString(), protocolEvent.Link?.Name);
        if (protocolEvent.Context is IDelivery delivery && delivery.UserToken is Amqp.Listener.ReceiveContext context &&
            context.UserToken is Guid lockToken)
        {
            delivery.Tag = lockToken.ToByteArray();
        }
    }
}

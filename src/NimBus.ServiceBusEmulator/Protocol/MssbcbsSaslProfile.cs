using Amqp;
using Amqp.Sasl;
using Amqp.Types;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class MssbcbsSaslProfile() : SaslProfile("MSSBCBS")
{
    protected override ITransport UpgradeTransport(ITransport transport) => transport;

    protected override DescribedList GetStartCommand(string hostname) =>
        new SaslInit { Mechanism = Mechanism };

    protected override DescribedList? OnCommand(DescribedList command)
    {
        if (command is SaslInit)
        {
            return new SaslOutcome { Code = SaslCode.Ok };
        }

        throw new AmqpException("amqp:not-allowed", "Unexpected SASL command.");
    }
}

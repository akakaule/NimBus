using Amqp;
using Amqp.Framing;
using Amqp.Listener;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class CbsRequestProcessor : IRequestProcessor
{
    internal const string CbsStatusCode = "status-code";

    public int Credit => 100;

    public void Process(RequestContext requestContext)
    {
        EmulatorDiagnostics.Write("CBS put-token");
        var response = new Message
        {
            ApplicationProperties = new ApplicationProperties(),
            BodySection = new AmqpValue { Value = "Accepted" },
        };
        response.ApplicationProperties[CbsStatusCode] = 202;
        response.ApplicationProperties["status-description"] = "Accepted";
        requestContext.Complete(response);
    }
}

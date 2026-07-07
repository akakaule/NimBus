using CloudEventsInterop.Contracts.Endpoints;
using NimBus.Core;

namespace CloudEventsInterop.Contracts
{
    /// <summary>
    /// Declarative topology for the CloudEvents interoperability sample. Provisioned by
    /// <c>CloudEventsInterop.Provisioner</c> via <c>ServiceBusTopologyProvisioner</c>.
    /// </summary>
    public class SamplePlatform : Platform
    {
        public SamplePlatform()
        {
            AddEndpoint(new SalesEndpoint());
            AddEndpoint(new InvoicingEndpoint());
        }
    }
}

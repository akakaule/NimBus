using CloudEventsInterop.Contracts.Events;
using NimBus.Core.Endpoints;

namespace CloudEventsInterop.Contracts.Endpoints
{
    /// <summary>
    /// Subscriber endpoint that consumes <see cref="InvoiceCreated"/> events. NimBus topology
    /// provisioning creates a forward subscription from <see cref="SalesEndpoint"/>'s topic into
    /// this endpoint's own topic/subscription, so this endpoint never subscribes to the producer
    /// directly (see docs/asyncapi-mapping.md for the general topology shape).
    /// </summary>
    public class InvoicingEndpoint : Endpoint
    {
        public InvoicingEndpoint()
        {
            Consumes<InvoiceCreated>();
        }

        public override ISystem System => new InvoicingSystem();

        public override string Description =>
            "Subscriber endpoint that processes InvoiceCreated events for billing, reading CloudEvents metadata when present.";
    }

    internal sealed class InvoicingSystem : ISystem
    {
        public string SystemId => "Invoicing";
    }
}

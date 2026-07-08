using CloudEventsInterop.Contracts.Events;
using NimBus.Core.Endpoints;

namespace CloudEventsInterop.Contracts.Endpoints
{
    /// <summary>
    /// Publisher endpoint that produces <see cref="InvoiceCreated"/> events. Its NimBus topic
    /// ("SalesEndpoint") is also where <c>samples/CloudEventsInterop</c> provisions the extra
    /// raw-capture subscription used by the non-NimBus consumer (step 4 of the demo).
    /// </summary>
    public class SalesEndpoint : Endpoint
    {
        public SalesEndpoint()
        {
            Produces<InvoiceCreated>();
        }

        public override ISystem System => new SalesSystem();

        public override string Description =>
            "Publisher endpoint that produces InvoiceCreated events, optionally emitted as CloudEvents 1.0 messages.";
    }

    internal sealed class SalesSystem : ISystem
    {
        public string SystemId => "Sales";
    }
}

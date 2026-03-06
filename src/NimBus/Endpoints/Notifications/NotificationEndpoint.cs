using NimBus.Core.Endpoints;
using NimBus.Events.Customers;
using NimBus.Events.Notifications;
using NimBus.Events.Payments;
using NimBus.Events.Shipping;

namespace NimBus.Endpoints.Notifications
{
    public class NotificationEndpoint : Endpoint
    {
        public NotificationEndpoint()
        {
            Consumes<CustomerRegistered>();
            Consumes<PaymentCaptured>();
            Consumes<ShipmentDispatched>();
            Produces<CustomerNotified>();
        }

        public override ISystem System => new NotificationSystem();

        public override string Description =>
            "Example mixed-role endpoint that subscribes to business events and publishes outbound customer notifications.";
    }

    internal sealed class NotificationSystem : ISystem
    {
        public string SystemId => "Notifications";
    }
}

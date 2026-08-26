using Microsoft.Extensions.Logging;
using NimBus.Events.Orders;
using NimBus.SDK.EventHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspirePubSub.WarehouseSubscriber.Handlers
{
    /// <summary>
    /// Attaches delivery details to a shipment — and fails ~30% of the time on purpose.
    /// </summary>
    /// <remarks>
    /// This event carries PII, so its failures are also what the Endpoints page's
    /// redaction behaves against: a non-PiiReader sees the masked payload while
    /// still being able to triage the error.
    /// </remarks>
    public partial class WarehouseDeliveryDetailsHandler : IEventHandler<OrderDeliveryDetailsCaptured>
    {
        private readonly ILogger<WarehouseDeliveryDetailsHandler> _logger;

        public WarehouseDeliveryDetailsHandler(ILogger<WarehouseDeliveryDetailsHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(OrderDeliveryDetailsCaptured message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            if (FlakyWarehouse.ShouldFail())
            {
                LogRoutingFailed(_logger, message.OrderId);
                throw new InvalidOperationException(
                    $"Warehouse could not route the shipment for order {message.OrderId}.");
            }

            // Never log the PII fields themselves — only the operational ones.
            LogRouted(_logger, message.OrderId, message.CarrierPreference, message.DeliveryWindow);
            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Routed shipment: OrderId={OrderId}, Carrier={Carrier}, Window={Window}")]
        private static partial void LogRouted(ILogger logger, Guid orderId, string carrier, string window);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Shipment routing failed for OrderId={OrderId} (simulated warehouse flakiness)")]
        private static partial void LogRoutingFailed(ILogger logger, Guid orderId);
    }
}

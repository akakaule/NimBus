using Microsoft.Extensions.Logging;
using NimBus.Events.Orders;
using NimBus.SDK.EventHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspirePubSub.WarehouseSubscriber.Handlers
{
    /// <summary>
    /// Reserves stock for a placed order — and fails ~30% of the time on purpose.
    /// </summary>
    public partial class WarehouseOrderPlacedHandler : IEventHandler<OrderPlaced>
    {
        private readonly ILogger<WarehouseOrderPlacedHandler> _logger;

        public WarehouseOrderPlacedHandler(ILogger<WarehouseOrderPlacedHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(OrderPlaced message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            // An explicit request to fail still wins, so the publisher's
            // SimulateFailure flag behaves the same on both endpoints.
            if (message.SimulateFailure || FlakyWarehouse.ShouldFail())
            {
                LogPickFailed(_logger, message.OrderId);
                throw new InvalidOperationException(
                    $"Warehouse could not reserve stock for order {message.OrderId}.");
            }

            LogReserved(_logger, message.OrderId, message.CustomerId, message.SalesChannel);
            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Reserved stock for OrderPlaced: OrderId={OrderId}, CustomerId={CustomerId}, Channel={Channel}")]
        private static partial void LogReserved(ILogger logger, Guid orderId, Guid customerId, string channel);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Stock reservation failed for OrderId={OrderId} (simulated warehouse flakiness)")]
        private static partial void LogPickFailed(ILogger logger, Guid orderId);
    }
}

using Microsoft.Extensions.Logging;
using NimBus.Core.Logging;
using NimBus.Events.Orders;
using NimBus.SDK.EventHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspirePubSub.Subscriber.Handlers
{
    public partial class OrderPlacedHandler : IEventHandler<OrderPlaced>
    {
        private readonly ILogger<OrderPlacedHandler> _logger;

        public OrderPlacedHandler(ILogger<OrderPlacedHandler> logger)
        {
            _logger = logger;
        }

        public Task Handle(OrderPlaced message, NimBus.Core.Logging.ILogger logger, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            if (message.SimulateFailure)
                throw new InvalidOperationException($"Simulated failure for order {message.OrderId}");

            LogOrderPlaced(_logger, message.OrderId, message.CustomerId, message.TotalAmount, message.CurrencyCode, message.SalesChannel);
            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Received OrderPlaced: OrderId={OrderId}, CustomerId={CustomerId}, Amount={Amount} {Currency}, Channel={Channel}")]
        private static partial void LogOrderPlaced(Microsoft.Extensions.Logging.ILogger logger, Guid orderId, Guid customerId, decimal amount, string currency, string channel);
    }
}

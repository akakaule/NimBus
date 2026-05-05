using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;
using RabbitMqOnPrem.Contracts.Events;

namespace RabbitMqOnPrem.Subscriber.Handlers;

public partial class CustomerCreatedHandler : IEventHandler<CustomerCreated>
{
    private readonly ILogger<CustomerCreatedHandler> _logger;

    public CustomerCreatedHandler(ILogger<CustomerCreatedHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(CustomerCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        if (message.SimulateFailure)
            throw new InvalidOperationException($"Simulated failure for customer {message.CustomerId}");

        LogCustomerCreated(_logger, message.CustomerId, message.Name, message.Email);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Received CustomerCreated: CustomerId={CustomerId}, Name={Name}, Email={Email}")]
    private static partial void LogCustomerCreated(Microsoft.Extensions.Logging.ILogger logger, Guid customerId, string name, string email);
}

using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace DataPlatform.Adapter.Functions.Handlers;

/// <summary>
/// Records inbound ErpCustomerCreated events for the data-platform sink.
/// A real downstream lake / warehouse would write to ADLS / Snowflake here;
/// for the demo, a structured log line gives operators an observable signal
/// without coupling the sample to extra infrastructure.
/// </summary>
public sealed class ErpCustomerCreatedHandler(
    ILogger<ErpCustomerCreatedHandler> logger)
    : IEventHandler<ErpCustomerCreated>
{
    public Task Handle(
        ErpCustomerCreated message,
        IEventHandlerContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DataPlatform received ErpCustomerCreated: AccountId={AccountId} CustomerNumber={CustomerNumber} LegalName={LegalName} CountryCode={CountryCode}",
            message.AccountId,
            message.CustomerNumber,
            message.LegalName,
            message.CountryCode);

        return Task.CompletedTask;
    }
}

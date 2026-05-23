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
    // Demo throttle: make every ingestion take ~10 s so messages visibly pile
    // up in Pending and the WebApp's Queue Time / TimingBar surface populates
    // with non-zero values. Combined with host.json maxConcurrentSessions=1
    // this serialises processing across all sessions — exactly one message
    // every ~10 s. Remove (or shorten) if the adapter is ever lifted out of
    // the demo.
    private static readonly TimeSpan DemoIngestionDelay = TimeSpan.FromSeconds(10);

    public async Task Handle(
        ErpCustomerCreated message,
        IEventHandlerContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Throttling DataPlatform ingestion of {EventTypeId} for {EventId} by {Delay} (demo).",
            context.EventType,
            context.EventId,
            DemoIngestionDelay);
        await Task.Delay(DemoIngestionDelay, cancellationToken);

        logger.LogInformation(
            "DataPlatform received ErpCustomerCreated: AccountId={AccountId} CustomerNumber={CustomerNumber} LegalName={LegalName} CountryCode={CountryCode}",
            message.AccountId,
            message.CustomerNumber,
            message.LegalName,
            message.CountryCode);
    }
}

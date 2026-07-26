using CrmErpDemo.Contracts.Commands;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

/// <summary>
/// Executes the PlaceCustomerOnCreditHold command (command showcase). Commands
/// derive from Event, so this is an ordinary event handler picked up by
/// AddHandlersFromAssemblyContaining — the command-ness lives in the contract:
/// exactly one consumer (this endpoint), enforced at provisioning time.
/// </summary>
public sealed class PlaceCustomerOnCreditHoldHandler(
    IErpApiClient erp,
    IServiceModeClient modeClient,
    ILogger<PlaceCustomerOnCreditHoldHandler> logger)
    : IEventHandler<PlaceCustomerOnCreditHold>
{
    public async Task Handle(PlaceCustomerOnCreditHold message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        await ErrorModeGuard.ThrowIfEnabledAsync(modeClient, context, logger, cancellationToken);

        var applied = await erp.PlaceCreditHoldByCrmIdAsync(message.AccountId, message.Reason, cancellationToken);
        if (applied)
        {
            logger.LogInformation(
                "Placed credit hold on ERP customer for CRM account {AccountId} (reason: {Reason})",
                message.AccountId, message.Reason ?? "<none>");
        }
        else
        {
            // No linked ERP customer yet — the command arrived before the account
            // sync completed. Complete normally; the operator can re-issue the hold.
            logger.LogWarning(
                "Credit hold requested for CRM account {AccountId}, but no linked ERP customer exists",
                message.AccountId);
        }
    }
}

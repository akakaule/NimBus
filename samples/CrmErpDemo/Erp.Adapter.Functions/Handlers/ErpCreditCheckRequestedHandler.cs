using CrmErpDemo.Contracts.Dtos;
using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;

namespace Erp.Adapter.Functions.Handlers;

/// <summary>
/// Request/reply responder: answers CRM's synchronous credit check. Registered
/// explicitly via <c>sub.AddRequestHandler</c> in Program.cs (request handlers
/// are not assembly-scanned); the reply travels back on CrmEndpoint-reply.
/// </summary>
public sealed class ErpCreditCheckRequestedHandler(
    IErpApiClient erp,
    ILogger<ErpCreditCheckRequestedHandler> logger)
    : IRequestHandler<ErpCreditCheckRequested, ErpCreditCheckResult>
{
    public async Task<ErpCreditCheckResult> Handle(ErpCreditCheckRequested request, CancellationToken cancellationToken = default)
    {
        var customer = await erp.GetCustomerByCrmAccountIdAsync(request.AccountId, cancellationToken);

        var status = customer switch
        {
            null => "NotFound",
            { IsDeleted: true } => "Deleted",
            { CreditHold: true } => "OnHold",
            _ => "Active",
        };

        logger.LogInformation(
            "Credit check for CRM account {AccountId}: {Status} (requested by {RequestedBy})",
            request.AccountId, status, request.RequestedBy ?? "<unknown>");

        return new ErpCreditCheckResult
        {
            AccountId = request.AccountId,
            Approved = status == "Active",
            Status = status,
            CustomerNumber = customer?.CustomerNumber,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }
}

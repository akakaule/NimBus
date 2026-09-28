using BusinessCentral.Adapter.Clients;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.Core.Messages;

namespace BusinessCentral.Adapter.Handlers;

/// <summary>
/// Request/reply responder: a seller's live credit check. Registered explicitly with
/// <c>AddRequestHandler</c>; the reply goes back on the D365SalesEndpoint-reply subscription.
/// It never throws for a Business Central failure: a request is itself an audited message, and a
/// failed read must not fail the message (and trigger pointless retries) — the seller gets an
/// "unavailable" answer immediately instead.
/// </summary>
public sealed class D365CreditCheckRequestedHandler(IBusinessCentralClient bc, ILogger<D365CreditCheckRequestedHandler> logger)
    : IRequestHandler<D365CreditCheckRequested, BcCreditStatus>
{
    public async Task<BcCreditStatus> Handle(D365CreditCheckRequested request, CancellationToken cancellationToken = default)
    {
        try
        {
            var customer = await bc.GetCustomerAsync(request.BcCustomerId, cancellationToken);
            if (customer is null)
            {
                return new BcCreditStatus
                {
                    AccountId = request.AccountId,
                    Status = BcCreditStatus.Statuses.NotFound,
                    CustomerNumber = request.BcCustomerNumber,
                    Reason = $"Business Central has no customer {request.BcCustomerNumber}.",
                    CheckedAt = DateTimeOffset.UtcNow,
                };
            }

            var financial = await bc.GetCustomerFinancialDetailAsync(request.BcCustomerId, cancellationToken);
            var blocked = string.IsNullOrWhiteSpace(customer.Blocked) ? string.Empty : customer.Blocked.Trim();
            logger.LogInformation(
                "Credit check for customer {CustomerNumber}: limit {CreditLimit}, balance {BalanceDue}, blocked '{Blocked}'",
                customer.Number, customer.CreditLimit, customer.BalanceDue, blocked);

            return new BcCreditStatus
            {
                AccountId = request.AccountId,
                Status = BcCreditStatus.Statuses.Ok,
                CustomerNumber = customer.Number,
                CreditLimit = customer.CreditLimit,
                BalanceDue = customer.BalanceDue,
                OverdueAmount = financial?.OverdueAmount,
                AvailableCredit = customer.CreditLimit > 0 ? customer.CreditLimit - customer.BalanceDue : null,
                Blocked = blocked,
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (BusinessCentralException ex)
        {
            logger.LogWarning("Credit check for customer {CustomerNumber} could not reach Business Central: {Reason}", request.BcCustomerNumber, ex.Message);
            return new BcCreditStatus
            {
                AccountId = request.AccountId,
                Status = BcCreditStatus.Statuses.Unavailable,
                CustomerNumber = request.BcCustomerNumber,
                Reason = ex is BcThrottledException
                    ? "Business Central is throttling requests right now. Try again in a moment."
                    : "Business Central is unavailable right now (for example during an update window). Try again shortly.",
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }
    }
}

using System.ComponentModel;

namespace DynamicsBcDemo.Contracts.BusinessCentral;

/// <summary>
/// Reply to <see cref="D365Sales.D365CreditCheckRequested"/>. A plain class, not an event and not
/// in the catalog: replies travel on the requester's <c>{endpoint}-reply</c> subscription, outside
/// event routing and auditing.
/// </summary>
[Description("Synchronous reply to D365CreditCheckRequested. Not a catalog event; it travels on the reply subscription.")]
public class BcCreditStatus
{
    /// <summary>Outcome values for <see cref="Status"/>.</summary>
    public static class Statuses
    {
        /// <summary>BC answered with the customer's credit data.</summary>
        public const string Ok = "Ok";

        /// <summary>BC has no such customer.</summary>
        public const string NotFound = "NotFound";

        /// <summary>BC could not be reached (update window, throttling, network).</summary>
        public const string Unavailable = "Unavailable";
    }

    /// <summary>The CRM account the status refers to.</summary>
    public Guid AccountId { get; set; }

    /// <summary>One of <see cref="Statuses"/>.</summary>
    public string Status { get; set; } = Statuses.Ok;

    /// <summary>The BC customer number.</summary>
    public string? CustomerNumber { get; set; }

    /// <summary>customer.creditLimit.</summary>
    public decimal? CreditLimit { get; set; }

    /// <summary>customer.balanceDue.</summary>
    public decimal? BalanceDue { get; set; }

    /// <summary>Overdue part of the balance (customerFinancialDetail.overdueAmount).</summary>
    public decimal? OverdueAmount { get; set; }

    /// <summary>Credit limit minus balance; null when the customer has no credit limit.</summary>
    public decimal? AvailableCredit { get; set; }

    /// <summary>customer.blocked: empty, Ship, Invoice or All.</summary>
    public string Blocked { get; set; } = string.Empty;

    /// <summary>Why the status is not <see cref="Statuses.Ok"/>, in words a seller understands.</summary>
    public string? Reason { get; set; }

    /// <summary>When BC evaluated the status (responder clock).</summary>
    public DateTimeOffset CheckedAt { get; set; }
}

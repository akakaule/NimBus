using System.ComponentModel;

namespace CrmErpDemo.Contracts.Dtos;

/// <summary>
/// Reply payload for <see cref="Events.ErpCreditCheckRequested"/>. Deliberately a plain
/// class, not an <c>Event</c>, and not part of the platform catalog: replies travel on the
/// requester's <c>{endpoint}-reply</c> subscription, outside event routing and auditing.
/// </summary>
[Description("Synchronous reply to ErpCreditCheckRequested. Not a catalog event — travels on the reply subscription.")]
public class ErpCreditCheckResult
{
    /// <summary>The CRM account the result refers to.</summary>
    public Guid AccountId { get; set; }

    /// <summary>True when the customer exists, is not deleted, and carries no credit hold.</summary>
    public bool Approved { get; set; }

    /// <summary>One of: Active, OnHold, NotFound, Deleted.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The ERP customer number, when the customer exists.</summary>
    public string? CustomerNumber { get; set; }

    /// <summary>When ERP evaluated the standing (responder clock).</summary>
    public DateTimeOffset CheckedAt { get; set; }
}

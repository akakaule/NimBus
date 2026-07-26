using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Commands;

[Description("Command: place the ERP customer linked to this CRM account on credit hold. Imperative, exactly one consumer (ErpEndpoint) — platform validation fails provisioning if a second endpoint declares it consumed. Contrast with the past-tense notification events (CrmAccountCreated, ErpCustomerUpdated, ...).")]
[SessionKey(nameof(AccountId))]
public class PlaceCustomerOnCreditHold : Command
{
    [Required]
    [Description("The CRM account whose linked ERP customer must be placed on hold.")]
    public Guid AccountId { get; set; }

    [Description("Why the hold is being placed, recorded in the ERP audit trail.")]
    public string? Reason { get; set; }

    [Description("When the hold was requested (requester clock).")]
    public DateTimeOffset RequestedAt { get; set; }
}

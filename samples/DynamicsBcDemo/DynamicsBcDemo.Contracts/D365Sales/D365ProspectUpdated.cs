using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.D365Sales;

[Description("Published by Dynamics 365 Sales when a seller changes a prospect's master data after Business Central already knows the prospect (it has a quote). BC updates its prospect contact. Once the prospect has become a BC customer, BC owns the data and ignores the update.")]
[SessionKey(nameof(AccountId))]
public class D365ProspectUpdated : Event
{
    public static readonly D365ProspectUpdated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        Prospect = new ProspectDetails
        {
            Name = "Tailspin Marine Research Ltd",
            VatRegistrationNumber = "GB123456789",
            AddressLine1 = "Ocean Way 14",
            City = "Southampton",
            PostalCode = "SO14 3ZH",
            CountryCode = "GB",
        },
        UpdatedAt = new DateTimeOffset(2026, 9, 28, 9, 30, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account (accountid). Session key.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The prospect's master data after the change.")]
    public ProspectDetails Prospect { get; set; } = new();

    [Description("When the account was changed in CRM.")]
    public DateTimeOffset UpdatedAt { get; set; }
}

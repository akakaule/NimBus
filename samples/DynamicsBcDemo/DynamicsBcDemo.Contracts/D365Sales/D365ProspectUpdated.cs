using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.D365Sales;

[Description("Published by Dynamics 365 Sales when a prospect is created (a qualified lead) or when a seller changes a prospect CRM still owns. Business Central keeps the prospect as a contact, so a quote can be made for it. Once the account has received a quote, Business Central manages it and CRM stops sending changes.")]
[SessionKey(nameof(AccountId))]
public class D365ProspectUpdated : Event
{
    public static readonly D365ProspectUpdated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        Prospect = new ProspectDetails
        {
            Name = "Tailspin Marine Research",
            VatRegistrationNumber = "GB123456789",
            AddressLine1 = "Ocean Way 12",
            City = "Southampton",
            PostalCode = "SO14 3ZH",
            CountryCode = "GB",
            Phone = "+44 23 8000 1000",
            Website = "https://tailspin-marine.example",
        },
        PrimaryContact = new ContactPersonDetails
        {
            FullName = "Hannah Okafor",
            Email = "hannah.okafor@tailspin-marine.example",
            Phone = "+44 23 8000 1001",
        },
        UpdatedAt = new DateTimeOffset(2026, 9, 28, 9, 30, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account (accountid). Session key.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The prospect's master data.")]
    public ProspectDetails Prospect { get; set; } = new();

    [Description("The prospect's primary contact person.")]
    public ContactPersonDetails? PrimaryContact { get; set; }

    [Description("When the account was created or changed in CRM.")]
    public DateTimeOffset UpdatedAt { get; set; }
}

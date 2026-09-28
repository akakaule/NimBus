using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace DynamicsBcDemo.Contracts.BusinessCentral;

[Description("Published by Business Central for a person contact of a buying customer, for example in the initial sync at go-live. Business Central owns contacts; CRM creates or updates the contact under the customer's account. Field names follow the BC API v2.0 contact resource.")]
[SessionKey(nameof(AccountId))]
public class BcContactUpdated : Event
{
    public static readonly BcContactUpdated Example = new()
    {
        AccountId = Guid.Parse("bc000000-0000-4000-8000-0000000c0010"),
        ContactId = Guid.Parse("9e050000-0000-4000-8000-000000000101"),
        ContactNumber = "CT000021",
        FirstName = "Ingrid",
        Surname = "Solberg",
        Email = "ingrid.solberg@fabrikam-offshore.example",
        PhoneNumber = "+47 51 00 10 01",
        JobTitle = "Head of Marine Operations",
        CompanyContactId = Guid.Parse("c0a7c700-0000-4000-8000-000000000101"),
        CompanyName = "Fabrikam Offshore Energy",
        CustomerId = Guid.Parse("bc000000-0000-4000-8000-0000000c0010"),
        CustomerNumber = "C00010",
        ChangedAt = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("Session key: the customer's CRM account id when BC knows it, otherwise the BC customer id, so a contact always follows its customer.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("contact.id")]
    public Guid ContactId { get; set; }

    [Required]
    [Description("contact.number, e.g. CT000021.")]
    public string ContactNumber { get; set; } = string.Empty;

    [Sensitive]
    [Description("First name.")]
    public string? FirstName { get; set; }

    [Sensitive]
    [Required]
    [Description("Surname.")]
    public string Surname { get; set; } = string.Empty;

    [Sensitive]
    [Description("E-mail address.")]
    public string? Email { get; set; }

    [Sensitive]
    [Description("Phone number.")]
    public string? PhoneNumber { get; set; }

    [Description("Job title.")]
    public string? JobTitle { get; set; }

    [Required]
    [Description("The company contact the person belongs to.")]
    public Guid CompanyContactId { get; set; }

    [Required]
    [Description("The company's name.")]
    public string CompanyName { get; set; } = string.Empty;

    [Description("The BC customer behind the company.")]
    public Guid? CustomerId { get; set; }

    [Description("customer.number")]
    public string? CustomerNumber { get; set; }

    [Description("The company's CRM account id (AL extension field), when BC knows it.")]
    public Guid? CrmAccountId { get; set; }

    [Description("contact.lastModifiedDateTime")]
    public DateTimeOffset ChangedAt { get; set; }
}

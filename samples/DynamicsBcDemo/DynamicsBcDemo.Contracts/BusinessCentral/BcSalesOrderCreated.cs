using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.BusinessCentral;

[Description("Published by Business Central when a sales quote becomes a sales order (Make Order). CRM closes the opportunity as won with the order amount. Published after BcCustomerCreated in the same session, so CRM always links the customer first.")]
[SessionKey(nameof(AccountId))]
public class BcSalesOrderCreated : Event
{
    public static readonly BcSalesOrderCreated Example = new()
    {
        AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        CrmAccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105"),
        OrderId = Guid.Parse("0d000000-0000-4000-8000-000000001001"),
        OrderNumber = "S-ORD1001",
        QuoteNumber = "S-QUO1002",
        OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105"),
        ExternalDocumentNumber = "OPP-10025",
        CustomerId = Guid.Parse("bc000000-0000-4000-8000-0000000c0050"),
        CustomerNumber = "C00050",
        SalespersonCode = "AR",
        CurrencyCode = "EUR",
        TotalAmountExcludingTax = 204600m,
        OrderDate = new DateTime(2026, 9, 28),
    };

    [Required]
    [Description("Session key: the CRM account id, or the BC customer id for customers created in BC.")]
    public Guid AccountId { get; set; }

    [Description("The CRM account id (CRM reference field, AL extension).")]
    public Guid? CrmAccountId { get; set; }

    [Required]
    [Description("salesOrder.id")]
    public Guid OrderId { get; set; }

    [Required]
    [Description("salesOrder.number, e.g. S-ORD1001.")]
    public string OrderNumber { get; set; } = string.Empty;

    [Description("The quote the order was made from.")]
    public string? QuoteNumber { get; set; }

    [Description("The CRM opportunity (CRM reference field, AL extension).")]
    public Guid? OpportunityId { get; set; }

    [Description("salesOrder.externalDocumentNumber: the CRM opportunity number.")]
    public string? ExternalDocumentNumber { get; set; }

    [Required]
    [Description("salesOrder.customerId")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("salesOrder.customerNumber")]
    public string CustomerNumber { get; set; } = string.Empty;

    [Description("salesOrder.salesperson")]
    public string? SalespersonCode { get; set; }

    [Required]
    [Description("salesOrder.currencyCode")]
    public string CurrencyCode { get; set; } = "EUR";

    [Description("salesOrder.totalAmountExcludingTax")]
    public decimal TotalAmountExcludingTax { get; set; }

    [Description("salesOrder.orderDate")]
    public DateTime OrderDate { get; set; }
}

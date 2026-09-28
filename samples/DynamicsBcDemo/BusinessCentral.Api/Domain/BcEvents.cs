using BusinessCentral.Api.Data;
using DynamicsBcDemo.Contracts.BusinessCentral;

namespace BusinessCentral.Api.Domain;

/// <summary>Maps BC records to the contract events CRM consumes.</summary>
public static class BcEvents
{
    public static BcSalesQuoteCreated QuoteCreated(SalesQuote quote) => Fill(new BcSalesQuoteCreated(), quote);

    public static BcSalesQuoteUpdated QuoteUpdated(SalesQuote quote) => Fill(new BcSalesQuoteUpdated(), quote);

    public static BcCustomerCreated CustomerCreated(Customer customer) => Fill(new BcCustomerCreated(), customer);

    public static BcCustomerUpdated CustomerUpdated(Customer customer) => Fill(new BcCustomerUpdated(), customer);

    public static BcSalesOrderCreated OrderCreated(SalesOrder order) => new()
    {
        // Session key: the CRM account when BC knows it, else the customer — the same key the
        // customer events for this customer use, so order always follows customer.
        AccountId = order.CrmAccountId ?? order.CustomerId,
        CrmAccountId = order.CrmAccountId,
        OrderId = order.Id,
        OrderNumber = order.Number,
        QuoteNumber = order.QuoteNumber,
        OpportunityId = order.CrmOpportunityId,
        ExternalDocumentNumber = order.ExternalDocumentNumber,
        CustomerId = order.CustomerId,
        CustomerNumber = order.CustomerNumber,
        SalespersonCode = order.SalespersonCode,
        CurrencyCode = order.CurrencyCode,
        TotalAmountExcludingTax = order.TotalAmountExcludingTax,
        OrderDate = order.OrderDate,
    };

    private static T Fill<T>(T e, SalesQuote quote) where T : BcSalesQuoteEvent
    {
        e.AccountId = quote.CrmAccountId ?? quote.CustomerId ?? quote.Id;
        e.QuoteId = quote.Id;
        e.QuoteNumber = quote.Number;
        e.OpportunityId = quote.CrmOpportunityId ?? Guid.Empty;
        e.ExternalDocumentNumber = quote.ExternalDocumentNumber;
        e.Status = quote.Status;
        e.CustomerNumber = quote.CustomerNumber;
        e.SellToContactNumber = quote.SellToContactNumber;
        e.SellToName = quote.SellToName;
        e.SalespersonCode = quote.SalespersonCode;
        e.CurrencyCode = quote.CurrencyCode;
        e.TotalAmountExcludingTax = quote.TotalAmountExcludingTax;
        e.LineCount = quote.Lines.Count;
        e.DocumentDate = quote.DocumentDate;
        e.ValidUntilDate = quote.ValidUntilDate;
        e.SentDate = quote.SentDate;
        e.AcceptedDate = quote.AcceptedDate;
        e.ChangedAt = quote.LastModifiedDateTime;
        return e;
    }

    private static T Fill<T>(T e, Customer customer) where T : BcCustomerEvent
    {
        e.AccountId = customer.CrmAccountId ?? customer.Id;
        e.CrmAccountId = customer.CrmAccountId;
        e.CustomerId = customer.Id;
        e.CustomerNumber = customer.Number;
        e.DisplayName = customer.DisplayName;
        e.AddressLine1 = customer.AddressLine1;
        e.City = customer.City;
        e.PostalCode = customer.PostalCode;
        e.CountryCode = customer.CountryCode;
        e.PhoneNumber = customer.PhoneNumber;
        e.Website = customer.Website;
        e.TaxRegistrationNumber = customer.TaxRegistrationNumber;
        e.SalespersonCode = customer.SalespersonCode;
        e.CreditLimit = customer.CreditLimit;
        e.BalanceDue = customer.BalanceDue;
        e.Blocked = customer.Blocked;
        e.PaymentTermsCode = customer.PaymentTermsCode;
        e.CurrencyCode = customer.CurrencyCode;
        e.Origin = (BcCustomerOrigin)customer.Origin;
        e.ChangedAt = customer.LastModifiedDateTime;
        return e;
    }
}

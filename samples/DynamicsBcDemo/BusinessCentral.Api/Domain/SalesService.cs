using BusinessCentral.Api.Data;
using DynamicsBcDemo.Contracts.BusinessCentral;
using Microsoft.EntityFrameworkCore;

namespace BusinessCentral.Api.Domain;

/// <summary>
/// The Business Central rules the demo shows: quotes are made in BC, a prospect is quoted as a
/// contact, and the customer only comes into existence when a quote becomes an order. Every
/// operation records the events it raises in an <see cref="EventBuffer"/>, in publish order; the
/// caller saves and publishes them in one outbox transaction.
/// </summary>
public sealed class SalesService(BcDbContext db, INumberSeries numbers, TimeProvider clock)
{
    private const int QuoteValidityDays = 30;

    /// <summary>
    /// Creates the quote for a CRM opportunity. Idempotent per opportunity: while the opportunity
    /// has an open quote, a repeat of the same request revision changes nothing, and a newer
    /// revision re-quotes the draft.
    /// </summary>
    public async Task<QuoteRequestResult> HandleQuoteRequestAsync(QuoteRequest request, EventBuffer events, CancellationToken cancellationToken = default)
    {
        if (request.Lines.Count == 0)
            throw new BcBusinessRuleException("Application_NoLines", $"The quote request for {request.OpportunityNumber} has no lines.");

        var salesperson = await FindSalespersonAsync(request.SellerEmail, cancellationToken)
            ?? throw new BcBusinessRuleException(
                "Application_SalespersonNotFound",
                $"No salesperson with e-mail '{request.SellerEmail}' exists in Business Central. " +
                "Add the seller under Salespeople in Business Central, then resubmit the message.");

        var items = await LoadQuotableItemsAsync(request.Lines.Select(l => l.ItemNumber), cancellationToken);

        var existing = await db.SalesQuotes
            .Include(q => q.Lines)
            .Where(q => q.CrmOpportunityId == request.CrmOpportunityId
                && (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent))
            .OrderByDescending(q => q.LastModifiedDateTime)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == QuoteStatus.Draft && request.RequestRevision > existing.CrmRequestRevision)
            {
                ReplaceLines(existing, request.Lines.Select(l => ToLine(l, items[l.ItemNumber])));
                existing.CrmRequestRevision = request.RequestRevision;
                existing.SalespersonCode = salesperson.Code;
                Touch(existing);
                events.Add(BcEvents.QuoteUpdated(existing));
                return new QuoteRequestResult(existing, QuoteRequestOutcome.Updated);
            }

            return new QuoteRequestResult(existing, QuoteRequestOutcome.AlreadyExists);
        }

        Customer? customer = null;
        Contact? contact = null;
        if (!string.IsNullOrWhiteSpace(request.CustomerNumber))
        {
            customer = await db.Customers.FirstOrDefaultAsync(c => c.Number == request.CustomerNumber, cancellationToken)
                ?? throw new BcBusinessRuleException(
                    "Application_CustomerNotFound",
                    $"Customer {request.CustomerNumber} does not exist in Business Central.");
        }
        else
        {
            contact = await db.Contacts.FirstOrDefaultAsync(c => c.CrmAccountId == request.CrmAccountId, cancellationToken);
            if (contact?.CustomerId is Guid convertedCustomerId)
            {
                // The prospect already bought once: BC owns it now, so quote the customer and ignore
                // CRM's copy of the master data.
                customer = await db.Customers.SingleAsync(c => c.Id == convertedCustomerId, cancellationToken);
                contact = null;
            }
            else if (contact is null)
            {
                contact = new Contact
                {
                    Id = Guid.NewGuid(),
                    Number = await numbers.NextAsync(NumberSeriesKind.Contact, cancellationToken),
                    CrmAccountId = request.CrmAccountId,
                    CustomerTemplateCode = CustomerTemplates.DefaultFor(request.Prospect.CountryCode).Code,
                };
                ApplyProspect(contact, request.Prospect, request.ContactPerson);
                db.Contacts.Add(contact);
            }
            else
            {
                // Still a prospect: CRM owns the master data, so take its latest copy.
                ApplyProspect(contact, request.Prospect, request.ContactPerson);
            }
        }

        var today = clock.GetUtcNow().UtcDateTime.Date;
        var quote = new SalesQuote
        {
            Id = Guid.NewGuid(),
            Number = await numbers.NextAsync(NumberSeriesKind.SalesQuote, cancellationToken),
            ExternalDocumentNumber = request.OpportunityNumber,
            Description = request.OpportunityName,
            DocumentDate = today,
            ValidUntilDate = today.AddDays(QuoteValidityDays),
            CustomerId = customer?.Id,
            CustomerNumber = customer?.Number,
            SellToContactId = contact?.Id,
            SellToContactNumber = contact?.Number,
            SellToName = customer?.DisplayName ?? contact!.DisplayName,
            SalespersonCode = salesperson.Code,
            Status = QuoteStatus.Draft,
            CurrencyCode = request.CurrencyCode,
            CrmOpportunityId = request.CrmOpportunityId,
            CrmAccountId = request.CrmAccountId,
            CrmRequestRevision = request.RequestRevision,
        };
        ReplaceLines(quote, request.Lines.Select(l => ToLine(l, items[l.ItemNumber])));
        Touch(quote);
        db.SalesQuotes.Add(quote);

        events.Add(BcEvents.QuoteCreated(quote));
        return new QuoteRequestResult(quote, QuoteRequestOutcome.Created);
    }

    /// <summary>Replaces the quote's lines with the ones edited on the quote card.</summary>
    public async Task<SalesQuote> UpdateLinesAsync(Guid quoteId, IReadOnlyList<QuoteLineEdit> edits, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var quote = await LoadOpenQuoteAsync(quoteId, cancellationToken);
        if (edits.Count == 0)
            throw new BcBusinessRuleException("Application_NoLines", "A quote needs at least one line.");

        var items = await LoadQuotableItemsAsync(edits.Select(e => e.ItemNumber), cancellationToken);
        ReplaceLines(quote, edits.Select(e => new SalesQuoteLine
        {
            ItemNumber = e.ItemNumber,
            Description = items[e.ItemNumber].DisplayName,
            Quantity = e.Quantity,
            UnitPrice = e.UnitPrice,
            DiscountPercent = e.DiscountPercent,
        }));
        Touch(quote);
        events.Add(BcEvents.QuoteUpdated(quote));
        return quote;
    }

    /// <summary>Sends the quote to the customer (salesQuote action <c>send</c>).</summary>
    public async Task<SalesQuote> SendAsync(Guid quoteId, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var quote = await LoadOpenQuoteAsync(quoteId, cancellationToken);
        quote.Status = QuoteStatus.Sent;
        quote.SentDate = clock.GetUtcNow();
        Touch(quote);
        events.Add(BcEvents.QuoteUpdated(quote));
        return quote;
    }

    /// <summary>
    /// Converts the quote to a sales order (salesQuote action <c>makeOrder</c>). A prospect contact
    /// is converted to a customer first, from the chosen customer template — the moment BC takes
    /// ownership of the buying customer. Raises, in this order: customer created (if converted),
    /// quote accepted, order created.
    /// </summary>
    public async Task<MakeOrderResult> MakeOrderAsync(Guid quoteId, string? customerTemplateCode, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var quote = await LoadOpenQuoteAsync(quoteId, cancellationToken);
        var now = clock.GetUtcNow();

        Customer customer;
        var customerCreated = false;
        if (quote.CustomerId is Guid customerId)
        {
            customer = await db.Customers.SingleAsync(c => c.Id == customerId, cancellationToken);
        }
        else
        {
            var contact = await db.Contacts.SingleAsync(c => c.Id == quote.SellToContactId, cancellationToken);
            if (contact.CustomerId is Guid convertedCustomerId)
            {
                customer = await db.Customers.SingleAsync(c => c.Id == convertedCustomerId, cancellationToken);
            }
            else
            {
                var template = CustomerTemplates.Find(customerTemplateCode)
                    ?? CustomerTemplates.Find(contact.CustomerTemplateCode)
                    ?? CustomerTemplates.DefaultFor(contact.CountryCode);
                customer = new Customer
                {
                    Id = Guid.NewGuid(),
                    Number = await numbers.NextAsync(NumberSeriesKind.Customer, cancellationToken),
                    DisplayName = contact.DisplayName,
                    AddressLine1 = contact.AddressLine1,
                    City = contact.City,
                    PostalCode = contact.PostalCode,
                    CountryCode = contact.CountryCode,
                    PhoneNumber = contact.PhoneNumber,
                    Email = contact.ContactPersonEmail,
                    Website = contact.Website,
                    TaxRegistrationNumber = contact.VatRegistrationNumber,
                    SalespersonCode = quote.SalespersonCode,
                    CreditLimit = template.CreditLimit,
                    PaymentTermsCode = template.PaymentTermsCode,
                    CurrencyCode = quote.CurrencyCode,
                    CrmAccountId = contact.CrmAccountId,
                    Origin = (int)BcCustomerOrigin.ConvertedFromProspect,
                };
                Touch(customer);
                db.Customers.Add(customer);

                contact.CustomerId = customer.Id;
                contact.CustomerNumber = customer.Number;
                contact.LastModifiedDateTime = now;
                customerCreated = true;
                events.Add(BcEvents.CustomerCreated(customer));
            }

            quote.CustomerId = customer.Id;
            quote.CustomerNumber = customer.Number;
            quote.SellToName = customer.DisplayName;
        }

        var order = new SalesOrder
        {
            Id = Guid.NewGuid(),
            Number = await numbers.NextAsync(NumberSeriesKind.SalesOrder, cancellationToken),
            ExternalDocumentNumber = quote.ExternalDocumentNumber,
            QuoteNumber = quote.Number,
            OrderDate = now.UtcDateTime.Date,
            CustomerId = customer.Id,
            CustomerNumber = customer.Number,
            CustomerName = customer.DisplayName,
            SalespersonCode = quote.SalespersonCode,
            CurrencyCode = quote.CurrencyCode,
            TotalAmountExcludingTax = quote.TotalAmountExcludingTax,
            CrmOpportunityId = quote.CrmOpportunityId,
            CrmAccountId = quote.CrmAccountId ?? customer.CrmAccountId,
            LastModifiedDateTime = now,
            Lines = quote.Lines.OrderBy(l => l.Sequence).Select(l => new SalesOrderLine
            {
                Id = Guid.NewGuid(),
                Sequence = l.Sequence,
                ItemNumber = l.ItemNumber,
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                AmountExcludingTax = l.AmountExcludingTax,
            }).ToList(),
        };
        db.SalesOrders.Add(order);

        quote.Status = QuoteStatus.Accepted;
        quote.AcceptedDate = now.UtcDateTime.Date;
        quote.OrderNumber = order.Number;
        Touch(quote);

        events.Add(BcEvents.QuoteUpdated(quote));
        events.Add(BcEvents.OrderCreated(order));
        return new MakeOrderResult(order, customer, customerCreated);
    }

    /// <summary>Updates the customer card. BC owns this data; CRM mirrors it.</summary>
    public async Task<Customer> UpdateCustomerAsync(Guid customerId, CustomerEdit edit, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new BcBusinessRuleException("Internal_RecordNotFound", $"Customer {customerId} does not exist.", StatusCodes.Status404NotFound);

        var blocked = NormalizeBlocked(edit.Blocked);
        if (!string.IsNullOrWhiteSpace(edit.SalespersonCode)
            && !await db.Salespeople.AnyAsync(s => s.Code == edit.SalespersonCode, cancellationToken))
        {
            throw new BcBusinessRuleException("Application_SalespersonNotFound", $"Salesperson {edit.SalespersonCode} does not exist.");
        }

        customer.DisplayName = edit.DisplayName;
        customer.AddressLine1 = edit.AddressLine1;
        customer.City = edit.City;
        customer.PostalCode = edit.PostalCode;
        customer.CountryCode = edit.CountryCode;
        customer.PhoneNumber = edit.PhoneNumber;
        customer.Email = edit.Email;
        customer.Website = edit.Website;
        customer.SalespersonCode = string.IsNullOrWhiteSpace(edit.SalespersonCode) ? null : edit.SalespersonCode;
        customer.CreditLimit = edit.CreditLimit;
        customer.Blocked = blocked;
        customer.PaymentTermsCode = edit.PaymentTermsCode;
        Touch(customer);

        events.Add(BcEvents.CustomerUpdated(customer));
        return customer;
    }

    /// <summary>
    /// Applies CRM's changes to a prospect contact. Once the prospect has become a customer BC owns
    /// the data and refuses with 409 — the ownership rule, enforced where the data lives.
    /// </summary>
    public async Task<ProspectUpdateOutcome> UpdateProspectAsync(Guid crmAccountId, ProspectData prospect, ContactPersonData? contactPerson, CancellationToken cancellationToken = default)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.CrmAccountId == crmAccountId, cancellationToken);
        if (contact is null)
            return ProspectUpdateOutcome.NotFound;

        if (contact.CustomerId is not null)
        {
            throw new BcBusinessRuleException(
                "Application_OwnedByBusinessCentral",
                $"'{contact.DisplayName}' became customer {contact.CustomerNumber} in Business Central, which now owns its master data.",
                StatusCodes.Status409Conflict);
        }

        ApplyProspect(contact, prospect, contactPerson);
        return ProspectUpdateOutcome.Updated;
    }

    private async Task<Salesperson?> FindSalespersonAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return await db.Salespeople.FirstOrDefaultAsync(s => s.Email == normalized, cancellationToken);
    }

    private async Task<Dictionary<string, Item>> LoadQuotableItemsAsync(IEnumerable<string> itemNumbers, CancellationToken cancellationToken)
    {
        var wanted = itemNumbers.Distinct(StringComparer.Ordinal).ToList();
        var items = await db.Items.Where(i => wanted.Contains(i.Number)).ToDictionaryAsync(i => i.Number, StringComparer.Ordinal, cancellationToken);

        var missing = wanted.FirstOrDefault(n => !items.ContainsKey(n));
        if (missing is not null)
            throw new BcBusinessRuleException("Application_ItemNotFound", $"Item {missing} does not exist in Business Central.");

        var blocked = items.Values.FirstOrDefault(i => i.Blocked);
        if (blocked is not null)
        {
            throw new BcBusinessRuleException(
                "Application_ItemBlocked",
                $"Item {blocked.Number} ({blocked.DisplayName}) is blocked in Business Central and can't be quoted. " +
                "Unblock the item or quote a replacement, then resubmit.");
        }

        return items;
    }

    private async Task<SalesQuote> LoadOpenQuoteAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var quote = await db.SalesQuotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken)
            ?? throw new BcBusinessRuleException("Internal_RecordNotFound", $"Sales quote {quoteId} does not exist.", StatusCodes.Status404NotFound);

        if (quote.Status is QuoteStatus.Accepted or QuoteStatus.Expired)
        {
            throw new BcBusinessRuleException(
                "Application_QuoteClosed",
                $"Sales quote {quote.Number} is {quote.Status} and can no longer be changed.",
                StatusCodes.Status409Conflict);
        }

        return quote;
    }

    private static SalesQuoteLine ToLine(QuoteRequestLine requested, Item item) => new()
    {
        ItemNumber = item.Number,
        Description = item.DisplayName,
        Quantity = requested.Quantity,
        UnitPrice = item.UnitPrice,
        DiscountPercent = 0m,
    };

    private void ReplaceLines(SalesQuote quote, IEnumerable<SalesQuoteLine> lines)
    {
        foreach (var old in quote.Lines.ToList())
        {
            quote.Lines.Remove(old);
            db.SalesQuoteLines.Remove(old);
        }

        var sequence = 10000;
        foreach (var line in lines)
        {
            line.Id = Guid.NewGuid();
            line.QuoteId = quote.Id;
            line.Sequence = sequence;
            line.AmountExcludingTax = Math.Round(line.Quantity * line.UnitPrice * (1 - (line.DiscountPercent / 100m)), 2);
            quote.Lines.Add(line);
            // Add explicitly: found only through a tracked quote's collection, a line whose Guid key
            // is already set would be taken for an existing row and UPDATEd (0 rows affected).
            db.SalesQuoteLines.Add(line);
            sequence += 10000;
        }

        quote.TotalAmountExcludingTax = quote.Lines.Sum(l => l.AmountExcludingTax);
    }

    private static void ApplyProspect(Contact contact, ProspectData prospect, ContactPersonData? person)
    {
        contact.DisplayName = prospect.Name;
        contact.VatRegistrationNumber = prospect.VatRegistrationNumber;
        contact.AddressLine1 = prospect.AddressLine1;
        contact.City = prospect.City;
        contact.PostalCode = prospect.PostalCode;
        contact.CountryCode = prospect.CountryCode;
        contact.PhoneNumber = prospect.PhoneNumber;
        contact.Website = prospect.Website;
        if (person is not null)
        {
            contact.ContactPersonName = person.Name;
            contact.ContactPersonEmail = person.Email;
            contact.ContactPersonPhone = person.Phone;
        }

        contact.LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    private static string NormalizeBlocked(string? blocked)
    {
        var value = (blocked ?? string.Empty).Trim();
        return value switch
        {
            "" => string.Empty,
            _ when value.Equals("Ship", StringComparison.OrdinalIgnoreCase) => "Ship",
            _ when value.Equals("Invoice", StringComparison.OrdinalIgnoreCase) => "Invoice",
            _ when value.Equals("All", StringComparison.OrdinalIgnoreCase) => "All",
            _ => throw new BcBusinessRuleException("Application_InvalidBlocked", $"Blocked must be empty, Ship, Invoice or All, not '{blocked}'.", StatusCodes.Status400BadRequest),
        };
    }

    private void Touch(SalesQuote quote) => quote.LastModifiedDateTime = clock.GetUtcNow();

    private void Touch(Customer customer) => customer.LastModifiedDateTime = clock.GetUtcNow();
}

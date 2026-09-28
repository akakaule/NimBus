using BusinessCentral.Api.Data;
using DynamicsBcDemo.Contracts.BusinessCentral;
using Microsoft.EntityFrameworkCore;

namespace BusinessCentral.Api.Domain;

/// <summary>
/// The Business Central rules the demo shows: CRM's prospects and opportunities are kept in BC,
/// quotes are made in BC and linked to a CRM opportunity, a prospect is quoted as a contact, and the
/// customer only comes into existence when a quote becomes an order. Every operation records the
/// events it raises in an <see cref="EventBuffer"/>, in publish order; the caller saves and publishes
/// them in one outbox transaction.
/// </summary>
public sealed class SalesService(BcDbContext db, INumberSeries numbers, TimeProvider clock)
{
    private const int QuoteValidityDays = 30;

    /// <summary>
    /// Keeps CRM's prospect as a company contact, so a BC user can quote it: creates the contact the
    /// first time and updates it after that. Once the prospect has become a customer BC owns the data
    /// and refuses with 409 — the ownership rule, enforced where the data lives.
    /// </summary>
    public async Task<ProspectUpsertOutcome> UpsertProspectAsync(Guid crmAccountId, ProspectData prospect, ContactPersonData? contactPerson, CancellationToken cancellationToken = default)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.CrmAccountId == crmAccountId, cancellationToken);
        if (contact is null)
        {
            contact = new Contact
            {
                Id = Guid.NewGuid(),
                Number = await numbers.NextAsync(NumberSeriesKind.Contact, cancellationToken),
                Type = ContactType.Company,
                CrmAccountId = crmAccountId,
                CustomerTemplateCode = CustomerTemplates.DefaultFor(prospect.CountryCode).Code,
            };
            ApplyProspect(contact, prospect, contactPerson);
            db.Contacts.Add(contact);
            return ProspectUpsertOutcome.Created;
        }

        if (contact.CustomerId is not null)
        {
            throw new BcBusinessRuleException(
                "Application_OwnedByBusinessCentral",
                $"'{contact.DisplayName}' became customer {contact.CustomerNumber} in Business Central, which now owns its master data.",
                StatusCodes.Status409Conflict);
        }

        ApplyProspect(contact, prospect, contactPerson);
        return ProspectUpsertOutcome.Updated;
    }

    /// <summary>
    /// Keeps a CRM opportunity so a BC user can create a quote for it and link the two. The opportunity's
    /// seller must exist as a salesperson (matched by e-mail), because the quote gets that salesperson: a
    /// missing one is a data problem an operator fixes in BC, then resubmits the message. When CRM says
    /// the account is a BC customer that isn't linked to CRM yet (loaded at go-live), BC links it now.
    /// </summary>
    public async Task<CrmOpportunityUpsertOutcome> UpsertCrmOpportunityAsync(Guid crmOpportunityId, CrmOpportunityData data, CancellationToken cancellationToken = default)
    {
        var salesperson = await FindSalespersonAsync(data.SellerEmail, cancellationToken)
            ?? throw new BcBusinessRuleException(
                "Application_SalespersonNotFound",
                $"No salesperson with e-mail '{data.SellerEmail}' exists in Business Central. " +
                "Add the seller under Salespeople in Business Central, then resubmit the message.");

        Guid? customerId;
        if (data.BcCustomerId is Guid bcCustomerId)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == bcCustomerId, cancellationToken)
                ?? throw new BcBusinessRuleException(
                    "Application_CustomerNotFound",
                    $"Customer {bcCustomerId} does not exist in Business Central.");
            if (customer.CrmAccountId is null)
            {
                customer.CrmAccountId = data.CrmAccountId;
                Touch(customer);
            }

            customerId = customer.Id;
        }
        else
        {
            customerId = await db.Customers
                .Where(c => c.CrmAccountId == data.CrmAccountId)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var opportunity = await db.CrmOpportunities.FirstOrDefaultAsync(o => o.Id == crmOpportunityId, cancellationToken);
        var outcome = CrmOpportunityUpsertOutcome.Updated;
        if (opportunity is null)
        {
            opportunity = new CrmOpportunity { Id = crmOpportunityId };
            db.CrmOpportunities.Add(opportunity);
            outcome = CrmOpportunityUpsertOutcome.Created;
        }

        opportunity.Number = data.Number;
        opportunity.Name = data.Name;
        opportunity.CrmAccountId = data.CrmAccountId;
        opportunity.AccountName = data.AccountName;
        opportunity.CustomerId = customerId ?? opportunity.CustomerId;
        opportunity.SalespersonCode = salesperson.Code;
        opportunity.EstimatedValue = data.EstimatedValue;
        opportunity.CurrencyCode = data.CurrencyCode;
        opportunity.EstimatedCloseDate = data.EstimatedCloseDate;
        opportunity.ItemCategoryCode = string.IsNullOrWhiteSpace(data.ProductGroupCode) ? null : data.ProductGroupCode;
        // BC marks the opportunity Won itself when a linked quote is accepted; CRM never echoes that
        // win back, so an older "Open" from CRM must not undo it.
        if (opportunity.Status != CrmOpportunityStatus.Won)
            opportunity.Status = data.Status;
        opportunity.LastModifiedDateTime = clock.GetUtcNow();
        return outcome;
    }

    /// <summary>
    /// Creates a draft sales quote for a CRM opportunity — the BC user's "Create sales quote". The quote is
    /// linked to the opportunity (AL extension fields), made out to the customer when the account is one
    /// and otherwise to the prospect contact, gets the opportunity's salesperson, and starts without lines.
    /// </summary>
    public async Task<SalesQuote> CreateQuoteFromOpportunityAsync(Guid crmOpportunityId, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var opportunity = await db.CrmOpportunities.FirstOrDefaultAsync(o => o.Id == crmOpportunityId, cancellationToken)
            ?? throw new BcBusinessRuleException("Internal_RecordNotFound", $"CRM opportunity {crmOpportunityId} does not exist in Business Central.", StatusCodes.Status404NotFound);

        if (opportunity.Status != CrmOpportunityStatus.Open)
        {
            throw new BcBusinessRuleException(
                "Application_OpportunityClosed",
                $"CRM opportunity {opportunity.Number} is {opportunity.Status}; quotes are made for open opportunities only.",
                StatusCodes.Status409Conflict);
        }

        Customer? customer = null;
        Contact? contact = null;
        if (opportunity.CustomerId is Guid customerId)
        {
            customer = await db.Customers.SingleAsync(c => c.Id == customerId, cancellationToken);
        }
        else
        {
            customer = await db.Customers.FirstOrDefaultAsync(c => c.CrmAccountId == opportunity.CrmAccountId, cancellationToken);
            if (customer is null)
            {
                contact = await db.Contacts.FirstOrDefaultAsync(c => c.CrmAccountId == opportunity.CrmAccountId, cancellationToken);
                if (contact?.CustomerId is Guid convertedCustomerId)
                {
                    customer = await db.Customers.SingleAsync(c => c.Id == convertedCustomerId, cancellationToken);
                    contact = null;
                }
                else if (contact is null)
                {
                    throw new BcBusinessRuleException(
                        "Application_ProspectNotFound",
                        $"Business Central has no contact for {opportunity.AccountName} yet. The prospect arrives from Dynamics 365 when it is created there; check the integration in NimBus.");
                }
            }
        }

        var today = clock.GetUtcNow().UtcDateTime.Date;
        var quote = new SalesQuote
        {
            Id = Guid.NewGuid(),
            Number = await numbers.NextAsync(NumberSeriesKind.SalesQuote, cancellationToken),
            ExternalDocumentNumber = opportunity.Number,
            Description = opportunity.Name,
            DocumentDate = today,
            ValidUntilDate = today.AddDays(QuoteValidityDays),
            CustomerId = customer?.Id,
            CustomerNumber = customer?.Number,
            SellToContactId = contact?.Id,
            SellToContactNumber = contact?.Number,
            SellToName = customer?.DisplayName ?? contact!.DisplayName,
            SalespersonCode = opportunity.SalespersonCode,
            Status = QuoteStatus.Draft,
            CurrencyCode = opportunity.CurrencyCode,
            CrmOpportunityId = opportunity.Id,
            CrmAccountId = opportunity.CrmAccountId,
        };
        Touch(quote);
        db.SalesQuotes.Add(quote);

        RaiseQuoteCreated(events, quote);
        return quote;
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
        RaiseQuoteUpdated(events, quote);
        return quote;
    }

    /// <summary>Sends the quote to the customer (salesQuote action <c>send</c>).</summary>
    public async Task<SalesQuote> SendAsync(Guid quoteId, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var quote = await LoadOpenQuoteAsync(quoteId, cancellationToken);
        EnsureHasLines(quote);
        quote.Status = QuoteStatus.Sent;
        quote.SentDate = clock.GetUtcNow();
        Touch(quote);
        RaiseQuoteUpdated(events, quote);
        return quote;
    }

    /// <summary>
    /// Converts the quote to a sales order (salesQuote action <c>makeOrder</c>). A prospect contact is
    /// converted to a customer first, from the chosen customer template. Raises, in this order: customer
    /// created (if converted), then quote accepted, which CRM uses to close the opportunity as won. The
    /// order itself stays in BC: CRM doesn't need order history.
    /// </summary>
    public async Task<MakeOrderResult> MakeOrderAsync(Guid quoteId, string? customerTemplateCode, EventBuffer events, CancellationToken cancellationToken = default)
    {
        var quote = await LoadOpenQuoteAsync(quoteId, cancellationToken);
        EnsureHasLines(quote);
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

        if (quote.CrmOpportunityId is Guid crmOpportunityId
            && await db.CrmOpportunities.FirstOrDefaultAsync(o => o.Id == crmOpportunityId, cancellationToken) is { } opportunity)
        {
            opportunity.Status = CrmOpportunityStatus.Won;
            opportunity.CustomerId = customer.Id;
            opportunity.LastModifiedDateTime = now;
        }

        RaiseQuoteUpdated(events, quote);
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
    /// The go-live initial sync: raises every item category (CRM's product groups), then every customer,
    /// then every person contact of a customer, so CRM starts with BC's customer base. Each contact
    /// travels in its customer's session, after the customer. CRM upserts, so running it again changes
    /// nothing. People at prospects are left out: CRM owns prospects.
    /// </summary>
    public async Task<InitialSyncResult> BuildInitialSyncAsync(EventBuffer events, CancellationToken cancellationToken = default)
    {
        var categories = await db.ItemCategories.OrderBy(c => c.Code).ToListAsync(cancellationToken);
        foreach (var category in categories)
            events.Add(BcEvents.ItemCategoryUpdated(category));

        var customers = await db.Customers.OrderBy(c => c.Number).ToListAsync(cancellationToken);
        foreach (var customer in customers)
            events.Add(BcEvents.CustomerUpdated(customer));

        var customersById = customers.ToDictionary(c => c.Id);
        var customerCompanies = await db.Contacts
            .Where(c => c.Type == ContactType.Company && c.CustomerId != null)
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var people = await db.Contacts
            .Where(c => c.Type == ContactType.Person && c.CompanyContactId != null)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var contacts = 0;
        foreach (var person in people)
        {
            if (customerCompanies.TryGetValue(person.CompanyContactId!.Value, out var company)
                && customersById.TryGetValue(company.CustomerId!.Value, out var customer))
            {
                events.Add(BcEvents.ContactUpdated(person, company, customer));
                contacts++;
            }
        }

        return new InitialSyncResult(categories.Count, customers.Count, contacts);
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
                "Unblock the item or quote a replacement.");
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

    private static void EnsureHasLines(SalesQuote quote)
    {
        if (quote.Lines.Count == 0)
            throw new BcBusinessRuleException("Application_NoLines", $"Sales quote {quote.Number} has no lines yet. Add lines before sending it or making an order.");
    }

    // Only quotes linked to a CRM opportunity concern CRM; quotes BC makes on its own stay in BC.
    private static void RaiseQuoteCreated(EventBuffer events, SalesQuote quote)
    {
        if (quote.CrmOpportunityId is not null)
            events.Add(BcEvents.QuoteCreated(quote));
    }

    private static void RaiseQuoteUpdated(EventBuffer events, SalesQuote quote)
    {
        if (quote.CrmOpportunityId is not null)
            events.Add(BcEvents.QuoteUpdated(quote));
    }

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

    private void ApplyProspect(Contact contact, ProspectData prospect, ContactPersonData? person)
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

        contact.LastModifiedDateTime = clock.GetUtcNow();
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

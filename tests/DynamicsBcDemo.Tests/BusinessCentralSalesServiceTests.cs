#pragma warning disable CA1707, CA2007
using BusinessCentral.Api.Data;
using BusinessCentral.Api.Domain;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// The Business Central rules the demo shows: a prospect is quoted as a contact, the customer only
/// appears when the quote becomes an order, requests are idempotent per opportunity, and a missing
/// salesperson is refused with a message an operator can act on.
/// </summary>
[TestClass]
public sealed class BusinessCentralSalesServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProspectAccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105");
    private static readonly Guid OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105");

    private BcDbContext _db = null!;
    private SalesService _sales = null!;

    [TestInitialize]
    public void Initialize()
    {
        var options = new DbContextOptionsBuilder<BcDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new BcDbContext(options);
        BcDatabaseInitializer.Seed(_db, Now);
        _db.SaveChanges();
        _sales = new SalesService(_db, new InMemoryNumberSeries(), new FixedTimeProvider(Now));
    }

    // MSTest disposes the test class after each test.
    public void Dispose() => _db.Dispose();

    [TestMethod]
    public async Task QuoteRequest_ForAProspect_QuotesAContact_AndCreatesNoCustomer()
    {
        var customersBefore = await _db.Customers.CountAsync();
        var events = new EventBuffer();

        var result = await RequestAsync(events, revision: 1);

        Assert.AreEqual(QuoteRequestOutcome.Created, result.Outcome);
        var contact = await _db.Contacts.SingleAsync(c => c.CrmAccountId == ProspectAccountId);
        Assert.AreEqual("CT000101", contact.Number);
        Assert.IsNull(contact.CustomerId, "A prospect stays a contact until it buys.");
        Assert.AreEqual(customersBefore, await _db.Customers.CountAsync());

        Assert.AreEqual("S-QUO1002", result.Quote.Number);
        Assert.AreEqual(contact.Id, result.Quote.SellToContactId);
        Assert.AreEqual("AR", result.Quote.SalespersonCode);
        Assert.AreEqual(QuoteStatus.Draft, result.Quote.Status);
        Assert.AreEqual(184000m + (3 * 9600m), result.Quote.TotalAmountExcludingTax);

        var created = (BcSalesQuoteCreated)events.Events.Single();
        Assert.AreEqual(ProspectAccountId, created.AccountId, "Quote events share the CRM account's session.");
        Assert.AreEqual(OpportunityId, created.OpportunityId);
    }

    [TestMethod]
    public async Task QuoteRequest_RepeatedWithTheSameRevision_ChangesNothing()
    {
        await RequestAsync(new EventBuffer(), revision: 1);
        var events = new EventBuffer();

        var repeat = await RequestAsync(events, revision: 1);

        Assert.AreEqual(QuoteRequestOutcome.AlreadyExists, repeat.Outcome);
        Assert.AreEqual(0, events.Events.Count);
        Assert.AreEqual(1, await _db.SalesQuotes.CountAsync(q => q.CrmOpportunityId == OpportunityId));
    }

    [TestMethod]
    public async Task QuoteRequest_WithANewerRevision_RequotesTheDraft()
    {
        await RequestAsync(new EventBuffer(), revision: 1);
        var events = new EventBuffer();

        var requote = await RequestAsync(events, revision: 2, ("LARS-AF5", 1m));

        Assert.AreEqual(QuoteRequestOutcome.Updated, requote.Outcome);
        Assert.AreEqual(1, await _db.SalesQuotes.CountAsync(q => q.CrmOpportunityId == OpportunityId));
        Assert.AreEqual(412000m, requote.Quote.TotalAmountExcludingTax);
        Assert.IsInstanceOfType<BcSalesQuoteUpdated>(events.Events.Single());
    }

    [TestMethod]
    public async Task QuoteRequest_ForASellerWithoutASalesperson_IsRefusedWithAnActionableMessage()
    {
        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() =>
            RequestAsync(new EventBuffer(), revision: 1, sellerEmail: SeedData.RobinHale.Email));

        Assert.AreEqual("Application_SalespersonNotFound", ex.Code);
        Assert.AreEqual(422, ex.StatusCode);
        StringAssert.Contains(ex.Message, SeedData.RobinHale.Email, StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "Salespeople", StringComparison.Ordinal);
        Assert.AreEqual(0, await _db.Contacts.CountAsync(), "A refused request leaves no half-created prospect.");
    }

    [TestMethod]
    public async Task QuoteRequest_WithABlockedItem_IsRefused()
    {
        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() =>
            RequestAsync(new EventBuffer(), revision: 1, ("CAM-HD", 2m)));

        Assert.AreEqual("Application_ItemBlocked", ex.Code);
    }

    [TestMethod]
    public async Task MakeOrder_ConvertsTheProspectOnce_AndRaisesCustomerThenQuoteThenOrderInOneSession()
    {
        var quote = (await RequestAsync(new EventBuffer(), revision: 1)).Quote;
        await _db.SaveChangesAsync();
        var events = new EventBuffer();

        var result = await _sales.MakeOrderAsync(quote.Id, CustomerTemplates.Export.Code, events);
        await _db.SaveChangesAsync();

        Assert.IsTrue(result.CustomerCreated);
        CollectionAssert.AreEqual(
            new[] { typeof(BcCustomerCreated), typeof(BcSalesQuoteUpdated), typeof(BcSalesOrderCreated) },
            events.Events.Select(e => e.GetType()).ToArray(),
            "CRM must link the customer before it closes the opportunity.");
        Assert.IsTrue(events.Events.All(e => e.GetSessionId() == ProspectAccountId.ToString()), "All three share the CRM account's session.");

        var customer = (BcCustomerCreated)events.Events[0];
        Assert.AreEqual(BcCustomerOrigin.ConvertedFromProspect, customer.Origin);
        Assert.AreEqual(ProspectAccountId, customer.CrmAccountId);
        Assert.AreEqual(CustomerTemplates.Export.CreditLimit, customer.CreditLimit);
        Assert.AreEqual(CustomerTemplates.Export.PaymentTermsCode, customer.PaymentTermsCode);
        Assert.AreEqual(QuoteStatus.Accepted, ((BcSalesQuoteUpdated)events.Events[1]).Status);
        Assert.AreEqual(quote.TotalAmountExcludingTax, ((BcSalesOrderCreated)events.Events[2]).TotalAmountExcludingTax);

        var contact = await _db.Contacts.SingleAsync(c => c.CrmAccountId == ProspectAccountId);
        Assert.AreEqual(result.Customer.Id, contact.CustomerId);
    }

    [TestMethod]
    public async Task MakeOrder_OnAnAcceptedQuote_IsRefused()
    {
        var quote = (await RequestAsync(new EventBuffer(), revision: 1)).Quote;
        await _db.SaveChangesAsync();
        await _sales.MakeOrderAsync(quote.Id, null, new EventBuffer());
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => _sales.MakeOrderAsync(quote.Id, null, new EventBuffer()));

        Assert.AreEqual("Application_QuoteClosed", ex.Code);
        Assert.AreEqual(409, ex.StatusCode);
    }

    [TestMethod]
    public async Task ProspectUpdate_AfterTheProspectBought_IsRefusedBecauseBusinessCentralOwnsTheCustomer()
    {
        var quote = (await RequestAsync(new EventBuffer(), revision: 1)).Quote;
        await _db.SaveChangesAsync();
        await _sales.MakeOrderAsync(quote.Id, null, new EventBuffer());
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() =>
            _sales.UpdateProspectAsync(ProspectAccountId, Prospect("Renamed in CRM"), null));

        Assert.AreEqual("Application_OwnedByBusinessCentral", ex.Code);
        Assert.AreEqual(409, ex.StatusCode);
    }

    [TestMethod]
    public async Task ProspectUpdate_ForAProspectBusinessCentralDoesNotKnow_IsNotFound()
    {
        var outcome = await _sales.UpdateProspectAsync(ProspectAccountId, Prospect("Tailspin Marine Research"), null);

        Assert.AreEqual(ProspectUpdateOutcome.NotFound, outcome);
    }

    private async Task<QuoteRequestResult> RequestAsync(EventBuffer events, int revision, params (string Item, decimal Quantity)[] lines) =>
        await RequestAsync(events, revision, SeedData.AlexRivera.Email, lines);

    private async Task<QuoteRequestResult> RequestAsync(EventBuffer events, int revision, string sellerEmail, params (string Item, decimal Quantity)[] lines)
    {
        var requested = lines.Length > 0 ? lines : [("WNCH-E20", 1m), ("CBL-TOW100", 3m)];
        var result = await _sales.HandleQuoteRequestAsync(
            new QuoteRequest(
                CrmOpportunityId: OpportunityId,
                CrmAccountId: ProspectAccountId,
                OpportunityNumber: "OPP-10025",
                OpportunityName: "ROV winch upgrade for research vessel",
                RequestRevision: revision,
                CustomerNumber: null,
                Prospect: Prospect("Tailspin Marine Research"),
                ContactPerson: new ContactPersonData("Hannah Okafor", "hannah.okafor@tailspin-marine.example", null),
                SellerEmail: sellerEmail,
                CurrencyCode: "EUR",
                Lines: requested.Select(l => new QuoteRequestLine(l.Item, l.Quantity, null)).ToList()),
            events);
        await _db.SaveChangesAsync();
        return result;
    }

    private static ProspectData Prospect(string name) =>
        new(name, "GB123456789", "Ocean Way 12", "Southampton", "SO14 3ZH", "GB", null, null);
}

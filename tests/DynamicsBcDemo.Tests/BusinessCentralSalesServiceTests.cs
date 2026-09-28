#pragma warning disable CA1707, CA2007
using BusinessCentral.Api.Data;
using BusinessCentral.Api.Domain;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// The Business Central rules the demo shows: CRM's prospects and opportunities are kept in BC, a BC
/// user makes the quote and links it to a CRM opportunity, a prospect is quoted as a contact, the
/// customer only appears when a quote becomes an order, and a missing salesperson is refused with a
/// message an operator can act on.
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
    public async Task ProspectUpsert_FirstTime_CreatesAProspectContact_AndNoCustomer()
    {
        var customersBefore = await _db.Customers.CountAsync();

        var outcome = await UpsertProspectAsync("Tailspin Marine Research");

        Assert.AreEqual(ProspectUpsertOutcome.Created, outcome);
        var contact = await _db.Contacts.SingleAsync(c => c.CrmAccountId == ProspectAccountId);
        Assert.AreEqual("CT000101", contact.Number);
        Assert.AreEqual(ContactType.Company, contact.Type);
        Assert.AreEqual("Hannah Okafor", contact.ContactPersonName);
        Assert.IsNull(contact.CustomerId, "A prospect stays a contact until it buys.");
        Assert.AreEqual(customersBefore, await _db.Customers.CountAsync());
    }

    [TestMethod]
    public async Task ProspectUpsert_Again_UpdatesTheSameContact()
    {
        await UpsertProspectAsync("Tailspin Marine Research");

        var outcome = await UpsertProspectAsync("Tailspin Marine Research Ltd");

        Assert.AreEqual(ProspectUpsertOutcome.Updated, outcome);
        var contact = await _db.Contacts.SingleAsync(c => c.CrmAccountId == ProspectAccountId);
        Assert.AreEqual("Tailspin Marine Research Ltd", contact.DisplayName);
    }

    [TestMethod]
    public async Task ProspectUpsert_AfterTheProspectBought_IsRefusedBecauseBusinessCentralOwnsTheCustomer()
    {
        var quote = await QuoteTailspinAsync();
        await _sales.MakeOrderAsync(quote.Id, null, new EventBuffer());
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => UpsertProspectAsync("Renamed in CRM"));

        Assert.AreEqual("Application_OwnedByBusinessCentral", ex.Code);
        Assert.AreEqual(409, ex.StatusCode);
    }

    [TestMethod]
    public async Task OpportunityUpsert_ForASellerWithoutASalesperson_IsRefusedWithAnActionableMessage()
    {
        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => UpsertOpportunityAsync(sellerEmail: SeedData.RobinHale.Email));

        Assert.AreEqual("Application_SalespersonNotFound", ex.Code);
        Assert.AreEqual(422, ex.StatusCode);
        StringAssert.Contains(ex.Message, SeedData.RobinHale.Email, StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "Salespeople", StringComparison.Ordinal);
        Assert.IsFalse(await _db.CrmOpportunities.AnyAsync(o => o.Id == OpportunityId), "A refused opportunity is not kept.");
    }

    [TestMethod]
    public async Task OpportunityUpsert_Again_UpdatesTheSameOpportunity()
    {
        Assert.AreEqual(CrmOpportunityUpsertOutcome.Created, await UpsertOpportunityAsync());

        var outcome = await UpsertOpportunityAsync(estimatedValue: 250000m);

        Assert.AreEqual(CrmOpportunityUpsertOutcome.Updated, outcome);
        var opportunity = await _db.CrmOpportunities.SingleAsync(o => o.Id == OpportunityId);
        Assert.AreEqual(250000m, opportunity.EstimatedValue);
        Assert.AreEqual("AR", opportunity.SalespersonCode);
        Assert.AreEqual("WINCH", opportunity.ItemCategoryCode);
    }

    [TestMethod]
    public async Task OpportunityUpsert_ForACustomerLoadedAtGoLive_LinksTheCustomerToItsCrmAccount()
    {
        var fabrikam = SeedData.Fabrikam.BcCustomer!;
        var crmAccountId = Guid.NewGuid();

        await UpsertOpportunityAsync(accountId: crmAccountId, bcCustomerId: fabrikam.CustomerId);

        var customer = await _db.Customers.SingleAsync(c => c.Id == fabrikam.CustomerId);
        Assert.AreEqual(crmAccountId, customer.CrmAccountId);
        var opportunity = await _db.CrmOpportunities.SingleAsync(o => o.Id == OpportunityId);
        Assert.AreEqual(fabrikam.CustomerId, opportunity.CustomerId);
    }

    [TestMethod]
    public async Task CreateQuote_ForAProspectsOpportunity_QuotesTheContact_WithTheOpportunitysSalesperson()
    {
        await UpsertProspectAsync("Tailspin Marine Research");
        await UpsertOpportunityAsync();
        var events = new EventBuffer();

        var quote = await _sales.CreateQuoteFromOpportunityAsync(OpportunityId, events);
        await _db.SaveChangesAsync();

        var contact = await _db.Contacts.SingleAsync(c => c.CrmAccountId == ProspectAccountId);
        Assert.AreEqual("S-QUO1002", quote.Number);
        Assert.AreEqual(contact.Id, quote.SellToContactId);
        Assert.IsNull(quote.CustomerId);
        Assert.AreEqual("AR", quote.SalespersonCode);
        Assert.AreEqual("OPP-10025", quote.ExternalDocumentNumber);
        Assert.AreEqual(QuoteStatus.Draft, quote.Status);
        Assert.AreEqual(0, quote.Lines.Count, "The BC user adds the lines.");

        var created = (BcSalesQuoteCreated)events.Events.Single();
        Assert.AreEqual(ProspectAccountId, created.AccountId, "Quote events share the CRM account's session.");
        Assert.AreEqual(OpportunityId, created.OpportunityId);
    }

    [TestMethod]
    public async Task CreateQuote_ForACustomersOpportunity_QuotesTheCustomer()
    {
        var warmUp = SeedData.Opportunities.Single(o => o.Number == "OPP-10099");

        var quote = await _sales.CreateQuoteFromOpportunityAsync(warmUp.OpportunityId, new EventBuffer());

        Assert.AreEqual(SeedData.WarmUp.BcCustomer!.CustomerId, quote.CustomerId);
        Assert.IsNull(quote.SellToContactId);
    }

    [TestMethod]
    public async Task CreateQuote_WhenBusinessCentralDoesNotKnowTheProspect_IsRefused()
    {
        await UpsertOpportunityAsync();

        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => _sales.CreateQuoteFromOpportunityAsync(OpportunityId, new EventBuffer()));

        Assert.AreEqual("Application_ProspectNotFound", ex.Code);
    }

    [TestMethod]
    public async Task SendAndMakeOrder_RefuseAQuoteWithoutLines()
    {
        await UpsertProspectAsync("Tailspin Marine Research");
        await UpsertOpportunityAsync();
        var quote = await _sales.CreateQuoteFromOpportunityAsync(OpportunityId, new EventBuffer());
        await _db.SaveChangesAsync();

        var send = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => _sales.SendAsync(quote.Id, new EventBuffer()));
        var order = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => _sales.MakeOrderAsync(quote.Id, null, new EventBuffer()));

        Assert.AreEqual("Application_NoLines", send.Code);
        Assert.AreEqual("Application_NoLines", order.Code);
    }

    [TestMethod]
    public async Task QuoteLines_WithABlockedItem_AreRefused()
    {
        var quote = await QuoteTailspinAsync();

        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() =>
            _sales.UpdateLinesAsync(quote.Id, [new QuoteLineEdit("CAM-HD", 2m, 8900m, 0m)], new EventBuffer()));

        Assert.AreEqual("Application_ItemBlocked", ex.Code);
    }

    [TestMethod]
    public async Task AQuoteWithoutACrmOpportunity_RaisesNoEvents()
    {
        var events = new EventBuffer();

        await _sales.UpdateLinesAsync(SeedData.LitwareFrameworkQuote.QuoteId, [new QuoteLineEdit("CBL-TOW100", 25m, 9600m, 0m)], events);

        Assert.AreEqual(0, events.Events.Count, "Quotes BC makes on its own stay in BC.");
    }

    [TestMethod]
    public async Task MakeOrder_ConvertsTheProspectOnce_AndRaisesCustomerThenQuoteAcceptedInOneSession()
    {
        var quote = await QuoteTailspinAsync();
        var events = new EventBuffer();

        var result = await _sales.MakeOrderAsync(quote.Id, CustomerTemplates.Export.Code, events);
        await _db.SaveChangesAsync();

        Assert.IsTrue(result.CustomerCreated);
        CollectionAssert.AreEqual(
            new[] { typeof(BcCustomerCreated), typeof(BcSalesQuoteUpdated) },
            events.Events.Select(e => e.GetType()).ToArray(),
            "CRM must know the customer before the accepted quote closes the opportunity; the order stays in BC.");
        Assert.IsTrue(events.Events.All(e => e.GetSessionId() == ProspectAccountId.ToString()), "Both share the CRM account's session.");

        var customer = (BcCustomerCreated)events.Events[0];
        Assert.AreEqual(BcCustomerOrigin.ConvertedFromProspect, customer.Origin);
        Assert.AreEqual(ProspectAccountId, customer.CrmAccountId);
        Assert.AreEqual(CustomerTemplates.Export.CreditLimit, customer.CreditLimit);
        Assert.AreEqual(QuoteStatus.Accepted, ((BcSalesQuoteUpdated)events.Events[1]).Status);

        var contact = await _db.Contacts.SingleAsync(c => c.CrmAccountId == ProspectAccountId);
        Assert.AreEqual(result.Customer.Id, contact.CustomerId);
        var opportunity = await _db.CrmOpportunities.SingleAsync(o => o.Id == OpportunityId);
        Assert.AreEqual(CrmOpportunityStatus.Won, opportunity.Status);
    }

    [TestMethod]
    public async Task MakeOrder_OnAnAcceptedQuote_IsRefused()
    {
        var quote = await QuoteTailspinAsync();
        await _sales.MakeOrderAsync(quote.Id, null, new EventBuffer());
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsExactlyAsync<BcBusinessRuleException>(() => _sales.MakeOrderAsync(quote.Id, null, new EventBuffer()));

        Assert.AreEqual("Application_QuoteClosed", ex.Code);
        Assert.AreEqual(409, ex.StatusCode);
    }

    [TestMethod]
    public async Task InitialSync_SendsItemCategoriesThenCustomersThenTheirContacts()
    {
        var events = new EventBuffer();

        var result = await _sales.BuildInitialSyncAsync(events);

        var customers = SeedData.BcCustomers.ToList();
        var people = customers.Sum(a => 1 + a.OtherContacts.Count);
        Assert.AreEqual(new InitialSyncResult(SeedData.ItemCategories.Count, customers.Count, people), result);

        var kinds = events.Events.Select(e => e.GetType()).ToList();
        var expected = Enumerable.Repeat(typeof(BcItemCategoryUpdated), SeedData.ItemCategories.Count)
            .Concat(Enumerable.Repeat(typeof(BcCustomerUpdated), customers.Count))
            .Concat(Enumerable.Repeat(typeof(BcContactUpdated), people))
            .ToList();
        CollectionAssert.AreEqual(expected, kinds);

        var customerSessions = events.Events.OfType<BcCustomerUpdated>().ToDictionary(c => c.CustomerId, c => c.GetSessionId());
        foreach (var contact in events.Events.OfType<BcContactUpdated>())
            Assert.AreEqual(customerSessions[contact.CustomerId!.Value], contact.GetSessionId(), $"{contact.ContactNumber} must follow its customer.");

        var prospectPerson = SeedData.Proseware.PrimaryContact.BcContactNumber;
        Assert.IsFalse(events.Events.OfType<BcContactUpdated>().Any(c => c.ContactNumber == prospectPerson), "CRM owns prospects; their people are not sent back.");
    }

    /// <summary>Tailspin qualified in CRM (prospect and opportunity sent to BC), then quoted in BC with lines.</summary>
    private async Task<SalesQuote> QuoteTailspinAsync()
    {
        await UpsertProspectAsync("Tailspin Marine Research");
        await UpsertOpportunityAsync();
        var quote = await _sales.CreateQuoteFromOpportunityAsync(OpportunityId, new EventBuffer());
        await _db.SaveChangesAsync();
        await _sales.UpdateLinesAsync(quote.Id, [new QuoteLineEdit("WNCH-E20", 1m, 184000m, 0m), new QuoteLineEdit("CBL-TOW100", 3m, 9600m, 0m)], new EventBuffer());
        await _db.SaveChangesAsync();
        return quote;
    }

    private async Task<ProspectUpsertOutcome> UpsertProspectAsync(string name)
    {
        var outcome = await _sales.UpsertProspectAsync(
            ProspectAccountId,
            new ProspectData(name, "GB123456789", "Ocean Way 12", "Southampton", "SO14 3ZH", "GB", null, null),
            new ContactPersonData("Hannah Okafor", "hannah.okafor@tailspin-marine.example", null));
        await _db.SaveChangesAsync();
        return outcome;
    }

    private async Task<CrmOpportunityUpsertOutcome> UpsertOpportunityAsync(
        string? sellerEmail = null, Guid? accountId = null, Guid? bcCustomerId = null, decimal estimatedValue = 210000m)
    {
        var outcome = await _sales.UpsertCrmOpportunityAsync(
            OpportunityId,
            new CrmOpportunityData(
                Number: "OPP-10025",
                Name: "ROV winch upgrade for research vessel",
                CrmAccountId: accountId ?? ProspectAccountId,
                AccountName: "Tailspin Marine Research",
                BcCustomerId: bcCustomerId,
                SellerEmail: sellerEmail ?? SeedData.AlexRivera.Email,
                EstimatedValue: estimatedValue,
                CurrencyCode: "EUR",
                EstimatedCloseDate: new DateTime(2026, 11, 27),
                ProductGroupCode: "WINCH",
                Status: CrmOpportunityStatus.Open));
        await _db.SaveChangesAsync();
        return outcome;
    }
}

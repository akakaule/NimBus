#pragma warning disable CA1707, CA2007
using D365Sales.Adapter.Clients;
using D365Sales.Adapter.Handlers;
using DynamicsBcDemo.Contracts.BusinessCentral;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// What the Dynamics 365 adapter writes to Dataverse for Business Central's events: the account is
/// handed to Business Central at its first quote, and an accepted quote wins the opportunity.
/// </summary>
[TestClass]
public sealed class D365SalesAdapterTests
{
    private static readonly Guid AccountId = Guid.Parse("acc00000-0000-4000-8000-000000000105");
    private static readonly Guid OpportunityId = Guid.Parse("0bb00000-0000-4000-8000-000000000105");

    [TestMethod]
    public async Task FirstQuote_HandsTheAccountToBusinessCentral_AndLinksTheOpportunity()
    {
        var dataverse = new RecordingDataverseClient();

        await new BcSalesQuoteCreatedHandler(dataverse).Handle(Quote<BcSalesQuoteCreated>("Draft"), null!, CancellationToken.None);

        var account = dataverse.Single("account", AccountId);
        Assert.AreEqual(2, account["cs_masterdataowner"], "From its first quote the account is managed in Business Central.");
        Assert.AreEqual("CT000101", account["cs_bccontactnumber"]);

        var opportunity = dataverse.Single("opportunity", OpportunityId);
        Assert.AreEqual("S-QUO1002", opportunity["cs_bcquotenumber"]);
        Assert.IsFalse(opportunity.ContainsKey("estimatedvalue"), "CRM owns the opportunity; only the quote status comes back.");
        Assert.AreEqual(0, dataverse.Wins.Count);
    }

    [TestMethod]
    public async Task AcceptedQuote_WinsTheOpportunity_WithTheQuoteTotal()
    {
        var dataverse = new RecordingDataverseClient();
        var accepted = Quote<BcSalesQuoteUpdated>("Accepted");
        accepted.TotalAmountExcludingTax = 203600m;
        accepted.AcceptedDate = new DateTime(2026, 9, 29);

        await new BcSalesQuoteUpdatedHandler(dataverse).Handle(accepted, null!, CancellationToken.None);

        var win = dataverse.Wins.Single();
        Assert.AreEqual(OpportunityId, win.OpportunityId);
        Assert.AreEqual(203600m, win.Revenue);
        Assert.AreEqual(new DateTime(2026, 9, 29), win.End);
        StringAssert.Contains(win.Subject, "S-QUO1002", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SentQuote_DoesNotWinTheOpportunity()
    {
        var dataverse = new RecordingDataverseClient();

        await new BcSalesQuoteUpdatedHandler(dataverse).Handle(Quote<BcSalesQuoteUpdated>("Sent"), null!, CancellationToken.None);

        Assert.AreEqual(0, dataverse.Wins.Count);
        Assert.AreEqual("Sent", dataverse.Single("opportunity", OpportunityId)["cs_bcquotestatus"]);
    }

    [TestMethod]
    public async Task ContactOfACustomerLoadedAtGoLive_IsBoundByItsBusinessCentralCustomerId()
    {
        var dataverse = new RecordingDataverseClient();
        var customerId = Guid.Parse("bc000000-0000-4000-8000-0000000c0010");
        var contact = new BcContactUpdated
        {
            AccountId = customerId,
            ContactId = Guid.NewGuid(),
            ContactNumber = "CT000021",
            FirstName = "Ingrid",
            Surname = "Solberg",
            CompanyContactId = Guid.NewGuid(),
            CompanyName = "Fabrikam Offshore Energy",
            CustomerId = customerId,
        };

        await new BcContactUpdatedHandler(dataverse).Handle(contact, null!, CancellationToken.None);

        var columns = dataverse.Single("contact", contact.ContactId);
        Assert.AreEqual($"/accounts(cs_bccustomerid={customerId})", columns["parentcustomerid_account@odata.bind"]);
        Assert.AreEqual("Solberg", columns["lastname"]);
    }

    [TestMethod]
    public async Task ItemCategory_BecomesAProductGroup()
    {
        var dataverse = new RecordingDataverseClient();
        var category = BcItemCategoryUpdated.Example;

        await new BcItemCategoryUpdatedHandler(dataverse).Handle(category, null!, CancellationToken.None);

        var columns = dataverse.Single("productgroup", category.ItemCategoryId);
        Assert.AreEqual(category.Code, columns["cs_code"]);
        Assert.AreEqual(category.DisplayName, columns["cs_name"]);
    }

    private static T Quote<T>(string status) where T : BcSalesQuoteEvent, new() => new()
    {
        AccountId = AccountId,
        QuoteId = Guid.Parse("b0e00000-0000-4000-8000-000000001002"),
        QuoteNumber = "S-QUO1002",
        OpportunityId = OpportunityId,
        ExternalDocumentNumber = "OPP-10025",
        Status = status,
        SellToContactNumber = "CT000101",
        CurrencyCode = "EUR",
        TotalAmountExcludingTax = 212800m,
        DocumentDate = new DateTime(2026, 9, 28),
    };

    private sealed class RecordingDataverseClient : IDataverseClient
    {
        private readonly List<(string Table, Guid Id, IDictionary<string, object?> Columns)> _writes = [];

        public List<(Guid OpportunityId, decimal Revenue, DateTime End, string Subject)> Wins { get; } = [];

        public IDictionary<string, object?> Single(string table, Guid id) =>
            _writes.Single(w => w.Table == table && w.Id == id).Columns;

        public Task PatchAccountAsync(Guid accountId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
            Record("account", accountId, columns);

        public Task UpsertAccountByBcCustomerIdAsync(Guid bcCustomerId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
            Record("account-by-bc", bcCustomerId, columns);

        public Task PatchOpportunityAsync(Guid opportunityId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
            Record("opportunity", opportunityId, columns);

        public Task UpsertBcQuoteAsync(Guid bcQuoteId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
            Record("bcquote", bcQuoteId, columns);

        public Task UpsertContactByBcContactIdAsync(Guid bcContactId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
            Record("contact", bcContactId, columns);

        public Task UpsertProductGroupAsync(Guid productGroupId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
            Record("productgroup", productGroupId, columns);

        public Task WinOpportunityAsync(Guid opportunityId, decimal actualRevenue, DateTime actualEnd, string subject, CancellationToken cancellationToken)
        {
            Wins.Add((opportunityId, actualRevenue, actualEnd, subject));
            return Task.CompletedTask;
        }

        private Task Record(string table, Guid id, IDictionary<string, object?> columns)
        {
            _writes.Add((table, id, columns));
            return Task.CompletedTask;
        }
    }
}

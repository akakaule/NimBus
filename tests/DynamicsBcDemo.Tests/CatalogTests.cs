#pragma warning disable CA1707, CA2007
using System.Globalization;
using System.Reflection;
using CrmErpDemo.Contracts;
using DynamicsBcDemo.Contracts;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Tests;

[TestClass]
public sealed class CatalogTests
{
    private static readonly DynamicsBcPlatformConfiguration Platform = new();

    private static IEnumerable<Type> MessageTypes() =>
        Platform.EventTypes.Select(t => t.GetEventClassType()).OfType<Type>();

    [TestMethod]
    public void Catalog_PassesCommandValidation()
    {
        var errors = PlatformValidation.ValidateCommandConsumers(Platform);

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
    }

    [TestMethod]
    [DataRow(nameof(D365ProspectUpdated), "BusinessCentralEndpoint")]
    [DataRow(nameof(D365OpportunityUpdated), "BusinessCentralEndpoint")]
    [DataRow(nameof(D365CreditCheckRequested), "BusinessCentralEndpoint")]
    [DataRow(nameof(BcCustomerCreated), "D365SalesEndpoint")]
    [DataRow(nameof(BcCustomerUpdated), "D365SalesEndpoint")]
    [DataRow(nameof(BcContactUpdated), "D365SalesEndpoint")]
    [DataRow(nameof(BcItemCategoryUpdated), "D365SalesEndpoint")]
    [DataRow(nameof(BcSalesQuoteCreated), "D365SalesEndpoint")]
    [DataRow(nameof(BcSalesQuoteUpdated), "D365SalesEndpoint")]
    public void EachFlow_ReachesTheOtherSystem(string eventTypeId, string consumer)
    {
        var eventType = Platform.EventTypes.Single(t => t.Id == eventTypeId);

        CollectionAssert.AreEqual(new[] { consumer }, Platform.GetConsumers(eventType).Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void Catalog_HasNoQuoteRequestAndNoOrderMessages()
    {
        // Quotes are made in Business Central, and order history stays there.
        var ids = Platform.EventTypes.Select(t => t.Id).ToList();

        Assert.AreEqual(9, ids.Count, string.Join(", ", ids));
        Assert.IsFalse(MessageTypes().Any(t => typeof(Command).IsAssignableFrom(t)), "CRM no longer commands Business Central to quote.");
    }

    [TestMethod]
    public void EveryMessage_IsOrderedPerCrmAccount_ExceptTheCreditCheckAndProductGroups()
    {
        foreach (var type in MessageTypes())
        {
            var sessionKey = type.GetCustomAttribute<SessionKeyAttribute>(inherit: true);
            if (type == typeof(D365CreditCheckRequested))
            {
                // A read-only request must never block the customer's session.
                Assert.IsNull(sessionKey, $"{type.Name} must not share the customer's session.");
            }
            else if (type == typeof(BcItemCategoryUpdated))
            {
                Assert.AreEqual(nameof(BcItemCategoryUpdated.Code), sessionKey?.PropertyName, "A product group belongs to no account; it is ordered per category.");
            }
            else
            {
                Assert.IsNotNull(sessionKey, $"{type.Name} has no [SessionKey].");
                Assert.AreEqual("AccountId", sessionKey.PropertyName, $"{type.Name} must be ordered per CRM account.");
            }
        }
    }

    [TestMethod]
    public void EventNames_CarryTheProducingSystemsPrefix()
    {
        foreach (var endpoint in Platform.Endpoints)
        {
            var prefix = endpoint.Id == "D365SalesEndpoint" ? "D365" : "Bc";
            foreach (var produced in endpoint.EventTypesProduced)
                StringAssert.StartsWith(produced.Id, prefix, $"{produced.Id} is produced by {endpoint.Id}.");
        }
    }

    [TestMethod]
    public void EventTypeIds_DoNotCollideWithTheOtherCatalogs()
    {
        // EventTypeId is the unqualified class name and is global to a Service Bus namespace.
        var ours = Platform.EventTypes.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = new CrmErpPlatformConfiguration().EventTypes
            .Concat(new NimBus.PlatformConfiguration().EventTypes)
            .Select(t => t.Id);

        CollectionAssert.AreEqual(Array.Empty<string>(), others.Where(ours.Contains).ToArray());
    }

    [TestMethod]
    public void EveryMessageExample_IsValid()
    {
        foreach (var type in MessageTypes())
        {
            var example = type.GetField("Example", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Event;
            Assert.IsNotNull(example, $"{type.Name} has no static Example (shown in nimbus-ops).");

            var result = example.TryValidate();
            Assert.IsTrue(result.IsValid, $"{type.Name}.Example is invalid.");
        }
    }

    [TestMethod]
    public void SeedData_IsConsistentAcrossBothSystems()
    {
        var accountIds = SeedData.Accounts.Select(a => a.AccountId).ToHashSet();
        var categoryCodes = SeedData.ItemCategories.Select(c => c.Code).ToHashSet();

        Assert.AreEqual(1, SeedData.Sellers.Count(s => s.BcSalespersonCode is null), "Exactly one seller is deliberately missing in BC (scene 6a).");
        Assert.AreEqual(SeedData.Sellers.Count(s => s.BcSalespersonCode is not null), SeedData.Sellers.Select(s => s.BcSalespersonCode).OfType<string>().Distinct().Count());
        Assert.IsTrue(SeedData.Items.All(i => categoryCodes.Contains(i.CategoryCode)), "Every item belongs to a product group.");

        foreach (var opportunity in SeedData.Opportunities)
        {
            var account = SeedData.Accounts.Single(a => a.AccountId == opportunity.AccountId);
            Assert.IsTrue(account.InCrm, $"{opportunity.Number} belongs to an account CRM holds.");
            var owner = SeedData.Sellers.Single(s => s.SystemUserId == opportunity.OwnerId);
            Assert.IsNotNull(owner.BcSalespersonCode, $"{opportunity.Number} is in BC too, so its seller must be a BC salesperson.");
        }

        var bcQuoteAccount = SeedData.Accounts.Single(a => a.AccountId == SeedData.LitwareFrameworkQuote.AccountId);
        Assert.IsNotNull(bcQuoteAccount.BcCustomer, "The BC-only quote belongs to a BC customer.");

        // Seeded BC contacts must never collide with the numbers BC's Contact series hands out (from CT000101).
        var contactNumbers = SeedData.Accounts
            .SelectMany(a => a.OtherContacts.Prepend(a.PrimaryContact).Select(p => p.BcContactNumber).Append(a.BcCompanyContactNumber))
            .ToList();
        Assert.AreEqual(contactNumbers.Count, contactNumbers.Distinct().Count(), string.Join(", ", contactNumbers));
        Assert.IsTrue(contactNumbers.All(n => int.Parse(n[2..], CultureInfo.InvariantCulture) < 101), string.Join(", ", contactNumbers));

        var qualifiedAccounts = SeedData.Leads.Select(l => l.QualifiedAccountId).ToList();
        Assert.AreEqual(qualifiedAccounts.Count, qualifiedAccounts.Distinct().Count());
        Assert.IsFalse(qualifiedAccounts.Any(accountIds.Contains), "A lead must qualify into a new account.");
        Assert.AreEqual(SeedData.RobinHale.SystemUserId, SeedData.CityPower.OwnerId, "Scene 6a qualifies the lead of the seller who is missing in BC.");
    }
}

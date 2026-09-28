#pragma warning disable CA1707, CA2007
using System.Reflection;
using CrmErpDemo.Contracts;
using DynamicsBcDemo.Contracts;
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
    private static readonly string[] BusinessCentralOnly = ["BusinessCentralEndpoint"];

    private static IEnumerable<Type> MessageTypes() =>
        Platform.EventTypes.Select(t => t.GetEventClassType()).OfType<Type>();

    [TestMethod]
    public void Catalog_PassesCommandValidation()
    {
        var errors = PlatformValidation.ValidateCommandConsumers(Platform);

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
    }

    [TestMethod]
    public void CreateBcSalesQuote_IsACommandConsumedOnlyByBusinessCentral()
    {
        var command = Platform.EventTypes.Single(t => t.Id == nameof(CreateBcSalesQuote));

        Assert.IsTrue(typeof(Command).IsAssignableFrom(command.GetEventClassType()));
        CollectionAssert.AreEqual(BusinessCentralOnly, Platform.GetConsumers(command).Select(e => e.Id).ToList());
    }

    [TestMethod]
    public void EveryEventAndCommand_IsOrderedPerCrmAccount_ExceptTheCreditCheckRequest()
    {
        foreach (var type in MessageTypes())
        {
            var sessionKey = type.GetCustomAttribute<SessionKeyAttribute>(inherit: true);
            if (type == typeof(D365CreditCheckRequested))
            {
                // A read-only request must never block the customer's session.
                Assert.IsNull(sessionKey, $"{type.Name} must not share the customer's session.");
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
            {
                var type = produced.GetEventClassType()!;
                if (typeof(Command).IsAssignableFrom(type))
                    continue; // Commands are named imperatively after their target (CreateBcSalesQuote).

                StringAssert.StartsWith(produced.Id, prefix, $"{produced.Id} is produced by {endpoint.Id}.");
            }
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
        var itemNumbers = SeedData.Items.Select(i => i.Number).ToHashSet();
        var accountIds = SeedData.Accounts.Select(a => a.AccountId).ToHashSet();
        var sellerIds = SeedData.Sellers.Select(s => s.SystemUserId).ToHashSet();

        Assert.AreEqual(1, SeedData.Sellers.Count(s => s.BcSalespersonCode is null), "Exactly one seller is deliberately missing in BC (scene 7a).");
        Assert.AreEqual(SeedData.Sellers.Count(s => s.BcSalespersonCode is not null), SeedData.Sellers.Select(s => s.BcSalespersonCode).OfType<string>().Distinct().Count());

        foreach (var opportunity in SeedData.Opportunities)
        {
            Assert.IsTrue(accountIds.Contains(opportunity.AccountId), opportunity.Number);
            Assert.IsTrue(sellerIds.Contains(opportunity.OwnerId), opportunity.Number);
            Assert.IsTrue(opportunity.Lines.All(l => itemNumbers.Contains(l.ItemNumber)), opportunity.Number);
            if (opportunity.Quote is not null)
            {
                var account = SeedData.Accounts.Single(a => a.AccountId == opportunity.AccountId);
                Assert.IsNotNull(account.BcCustomer, $"The seeded quote {opportunity.Quote.Number} must belong to a BC customer.");
            }
        }

        var qualifiedAccounts = SeedData.Leads.Select(l => l.QualifiedAccountId).ToList();
        Assert.AreEqual(qualifiedAccounts.Count, qualifiedAccounts.Distinct().Count());
        Assert.IsFalse(qualifiedAccounts.Any(accountIds.Contains), "A lead must qualify into a new account.");
    }
}

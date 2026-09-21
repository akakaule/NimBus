#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using CrmErpDemo.Contracts.Demo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The erp-web error-mode dropdown exists to feed Integration Intelligence distinct,
/// realistic failures. These pin the properties the showcase depends on: every reason maps
/// to an exception, the advertised NimBus disposition matches what the default classifier
/// actually does, and the recorded message carries a cue for its target category.
/// </summary>
[TestClass]
public sealed class ErpFailureReasonsTests
{
    [TestMethod]
    public void Catalog_HasUniqueIdsAndAKnownDefault()
    {
        var ids = ErpFailureReasons.All.Select(r => r.Id).ToList();
        CollectionAssert.AllItemsAreUnique(ids);
        Assert.IsNotNull(ErpFailureReasons.Find(ErpFailureReasons.Default));
        Assert.AreEqual(ErpFailureReasons.Default, ErpFailureReasons.All[0].Id, "the default must be first so the UI lists it first");
    }

    [TestMethod]
    public void Find_IsCaseInsensitiveAndNullSafe()
    {
        Assert.AreEqual("business_rule", ErpFailureReasons.Find("BUSINESS_RULE")!.Id);
        Assert.IsNull(ErpFailureReasons.Find(null));
        Assert.IsNull(ErpFailureReasons.Find(" "));
        Assert.IsNull(ErpFailureReasons.Find("no_such_reason"));
    }

    [TestMethod]
    public void CreateException_UnknownReason_FallsBackToGenericSoErrorModeStaysOn()
    {
        var ex = ErpFailureReasons.CreateException("no_such_reason", "CrmAccountCreated");
        Assert.IsInstanceOfType<HandlerErrorModeException>(ex);
    }

    [TestMethod]
    public void EveryReason_DispositionMatchesDefaultPermanentFailureClassifier()
    {
        var classifier = new DefaultPermanentFailureClassifier();
        foreach (var reason in ErpFailureReasons.All)
        {
            var ex = ErpFailureReasons.CreateException(reason.Id, "CrmAccountCreated");
            var expectedPermanent = reason.Disposition == "DeadLettered";
            Assert.AreEqual(
                expectedPermanent,
                classifier.IsPermanentFailure(ex),
                $"{reason.Id} advertises {reason.Disposition} but the classifier disagrees for {ex.GetType().Name}");
        }
    }

    [TestMethod]
    public void EveryReason_ProducesDistinctExceptionTypesWithEvidenceInTheMessage()
    {
        var byType = ErpFailureReasons.All
            .Select(r => ErpFailureReasons.CreateException(r.Id, "CrmContactCreated"))
            .GroupBy(e => e.GetType())
            .ToList();
        Assert.AreEqual(ErpFailureReasons.All.Count, byType.Count, "each reason should surface a different ErrorType in the WebApp");

        // NimBus stores only the message, never a stack trace, so the message is the evidence.
        foreach (var reason in ErpFailureReasons.All.Where(r => r.Id != ErpFailureReasons.Default))
        {
            var message = ErpFailureReasons.CreateException(reason.Id, "CrmContactCreated").Message;
            Assert.IsTrue(message.Length > 80, $"{reason.Id} message is too thin to classify: '{message}'");
        }
    }

    [TestMethod]
    public void ContractSchema_QuotesTheInboundEventType()
    {
        var message = ErpFailureReasons.CreateException("contract_schema", "CrmAccountUpdated").Message;
        StringAssert.Contains(message, "CrmAccountUpdated");
    }
}

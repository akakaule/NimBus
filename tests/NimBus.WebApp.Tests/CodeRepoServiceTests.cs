#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class CodeRepoServiceTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Missing_repository_omits_optional_search_link(string? repositoryUrl)
    {
        var service = new CodeRepoService(repositoryUrl);

        Assert.IsNull(service.GetSearchUrl("CrmContactCreated", "CrmErpDemo.Contracts.Events"));
    }

    [TestMethod]
    public void Configured_repository_preserves_search_link()
    {
        var service = new CodeRepoService("https://dev.azure.com/example/project/");

        Assert.AreEqual(
            "https://dev.azure.com/example/project/_search?type=code&text= class:CrmContactCreated AND namespace:CrmErpDemo.Contracts.Events",
            service.GetSearchUrl("CrmContactCreated", "CrmErpDemo.Contracts.Events"));
    }
}

#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Controllers;

namespace NimBus.WebApp.Tests;

/// <summary>
/// /api/dev/status must be a real, environment-gated route: before it existed,
/// the SPA fallback answered the probe with 200 index.html and the Dev Tools
/// tab leaked into production.
/// </summary>
[TestClass]
public class DevStatusTests
{
    private static DevImplementation CreateSut(string environmentName)
        => new(seedDataService: null!, new FakeEnv(environmentName));

    [TestMethod]
    public async Task Status_is_ok_in_development()
    {
        var result = await CreateSut(Environments.Development).GetDevStatusAsync();
        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
    }

    [TestMethod]
    public async Task Status_is_404_outside_development()
    {
        Assert.IsInstanceOfType(await CreateSut(Environments.Production).GetDevStatusAsync(), typeof(NotFoundResult));
        Assert.IsInstanceOfType(await CreateSut(Environments.Staging).GetDevStatusAsync(), typeof(NotFoundResult));
    }

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public FakeEnv(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "NimBus.WebApp";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

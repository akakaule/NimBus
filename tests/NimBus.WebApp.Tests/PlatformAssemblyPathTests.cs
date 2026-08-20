#pragma warning disable CA1707, CA2007

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.WebApp.Tests;

// Covers Startup.ResolvePlatformAssemblyPath: the NimBus:PlatformAssembly setting
// deployed by the bicep template is a file name relative to the app root, and the
// App Service working directory is not the app root — relative values must anchor
// to AppContext.BaseDirectory while absolute paths pass through unchanged.
[TestClass]
public sealed class PlatformAssemblyPathTests
{
    [TestMethod]
    public void Relative_path_resolves_against_the_app_base_directory()
    {
        var resolved = Startup.ResolvePlatformAssemblyPath("MyCompany.Catalog.dll");

        Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "MyCompany.Catalog.dll"), resolved);
    }

    [TestMethod]
    public void Relative_subdirectory_path_resolves_against_the_app_base_directory()
    {
        var resolved = Startup.ResolvePlatformAssemblyPath(Path.Combine("catalog", "MyCompany.Catalog.dll"));

        Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "catalog", "MyCompany.Catalog.dll"), resolved);
    }

    [TestMethod]
    public void Absolute_path_passes_through_unchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "MyCompany.Catalog.dll");

        Assert.AreEqual(absolute, Startup.ResolvePlatformAssemblyPath(absolute));
    }

    [TestMethod]
    public void Null_and_whitespace_pass_through_unchanged()
    {
        Assert.IsNull(Startup.ResolvePlatformAssemblyPath(null));
        Assert.AreEqual(string.Empty, Startup.ResolvePlatformAssemblyPath(string.Empty));
        Assert.AreEqual("   ", Startup.ResolvePlatformAssemblyPath("   "));
    }
}

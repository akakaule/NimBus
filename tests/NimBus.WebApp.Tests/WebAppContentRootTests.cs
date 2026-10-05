#pragma warning disable CA1707, CA2007
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The WebApp runs from its own directory when launched as a dotnet tool from somewhere else
/// (an adapter repository's AppHost), so the SPA and appsettings.json are still found.
/// </summary>
[TestClass]
public sealed class WebAppContentRootTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "nimbus-contentroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void AppBaseHasTheSpa_AndTheWorkingDirectoryDoesNot_UsesTheAppBase()
    {
        var appBase = CreateDirectory("tool", withSpa: true);
        var workingDirectory = CreateDirectory("apphost", withSpa: false);

        Assert.AreEqual(appBase, Program.ResolveContentRoot(workingDirectory, appBase));
    }

    [TestMethod]
    public void WorkingDirectoryHasTheSpa_KeepsTheDefault()
    {
        // dotnet run from the project directory, and App Service (where both are the same).
        var appBase = CreateDirectory("bin", withSpa: true);
        var workingDirectory = CreateDirectory("project", withSpa: true);

        Assert.IsNull(Program.ResolveContentRoot(workingDirectory, appBase));
    }

    [TestMethod]
    public void NeitherHasTheSpa_KeepsTheDefault()
    {
        // Unit-test hosts run without a built SPA.
        var appBase = CreateDirectory("bin", withSpa: false);
        var workingDirectory = CreateDirectory("tests", withSpa: false);

        Assert.IsNull(Program.ResolveContentRoot(workingDirectory, appBase));
    }

    private string CreateDirectory(string name, bool withSpa)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(withSpa ? Path.Combine(path, "ClientApp", "build", "public") : path);
        return path;
    }
}

#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class AspirePackageVersionTests
{
    private const string ExpectedAspireVersion = "13.4.6";

    [TestMethod]
    public void Aspire_SDK_And_Packages_Are_Pinned_To_Approved_Version()
    {
        var mismatches = new List<string>();

        foreach (var projectFile in EnumerateProjectFiles())
        {
            var document = XDocument.Load(projectFile);
            var root = document.Root;
            if (root == null)
            {
                continue;
            }

            var sdk = root.Attribute("Sdk")?.Value;
            if (sdk != null && sdk.StartsWith("Aspire.AppHost.Sdk/", StringComparison.Ordinal))
            {
                var actualVersion = sdk["Aspire.AppHost.Sdk/".Length..];
                if (!string.Equals(actualVersion, ExpectedAspireVersion, StringComparison.Ordinal))
                {
                    mismatches.Add($"{Relative(projectFile)} SDK {actualVersion}");
                }
            }

            var packageReferences = root
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference");

            foreach (var reference in packageReferences)
            {
                var packageName = reference.Attribute("Include")?.Value;
                if (packageName == null || !packageName.StartsWith("Aspire.", StringComparison.Ordinal))
                {
                    continue;
                }

                var actualVersion = reference.Attribute("Version")?.Value;
                if (!string.Equals(actualVersion, ExpectedAspireVersion, StringComparison.Ordinal))
                {
                    mismatches.Add($"{Relative(projectFile)} {packageName} {actualVersion ?? "<missing>"}");
                }
            }
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail(
                $"Aspire SDK and package references must be pinned to {ExpectedAspireVersion}: " +
                string.Join("; ", mismatches));
        }
    }

    private static IEnumerable<string> EnumerateProjectFiles()
    {
        var root = FindRepositoryRoot();
        return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }
}

#pragma warning disable CA1707
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class ArchitectureReferenceTests
{
    private const string ForbiddenAssembly = "Microsoft.Azure.Cosmos";

    [TestMethod]
    public void SC009_host_assemblies_do_not_reference_Cosmos_SDK()
    {
        var hostAssemblies = new[]
        {
            "NimBus.WebApp",
            "nb",
            "NimBus.Resolver",
            "NimBus.Manager",
        };

        var offenders = new List<string>();
        foreach (var assemblyName in hostAssemblies)
        {
            var path = FindBuiltAssembly(assemblyName);
            if (path == null)
            {
                Assert.Inconclusive($"Could not locate a built {assemblyName}.dll under src/**/bin. Build the host projects before running this architecture guard.");
            }

            var references = Assembly.LoadFrom(path).GetReferencedAssemblies();
            if (references.Any(r => string.Equals(r.Name, ForbiddenAssembly, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add(assemblyName);
            }
        }

        if (offenders.Count > 0)
        {
            Assert.Fail(
                "SC-009 violation: host assemblies must not reference Microsoft.Azure.Cosmos directly. Offenders: " +
                string.Join(", ", offenders));
        }
    }

    private static string? FindBuiltAssembly(string assemblyName)
    {
        var root = FindRepositoryRoot();
        var src = Path.Combine(root, "src");
        var candidates = Directory
            .EnumerateFiles(src, $"{assemblyName}.dll", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }
}

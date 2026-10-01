#pragma warning disable CA1707, CA2007
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Guards docs/building-adapters.md against drifting back into the gaps of issue #186: the complete
/// Worker shape omitting deferred replay, and the guide losing its resilience and testing sections.
/// The sample adapters' comments link to these sections by anchor.
/// </summary>
[TestClass]
public sealed class BuildingAdaptersGuideTests
{
    private static readonly string Guide = File.ReadAllText(LocateRepoFile(Path.Combine("docs", "building-adapters.md")))
        .Replace("\r\n", "\n", StringComparison.Ordinal);

    [TestMethod]
    public void CompleteWorkerShape_RunsDeferredReplay()
    {
        var shape = Section("## Complete Worker shape");

        StringAssert.Contains(shape, "AddNimBusDeferredProcessorHostedService(", StringComparison.Ordinal, "Every subscriber endpoint needs deferred replay; the complete shape must show it.");
        StringAssert.Contains(shape, "PrefetchCount = 0", StringComparison.Ordinal, "The complete shape uses a circuit breaker, so it must turn prefetch off.");
    }

    [TestMethod]
    [DataRow("## Resilience")]
    [DataRow("## Adapter testing")]
    [DataRow("## Run a local stack from packages")]
    public void Guide_KeepsTheSectionsTheSamplesLinkTo(string heading)
    {
        StringAssert.Contains(Guide, $"\n{heading}\n", StringComparison.Ordinal, $"docs/building-adapters.md must keep its '{heading}' section.");
    }

    [TestMethod]
    public void Guide_WarnsAboutHiddenHttpRetries()
    {
        StringAssert.Contains(Section("## Resilience"), "UseStandardResilienceHandler", StringComparison.Ordinal);
    }

    private static string Section(string heading)
    {
        var start = Guide.IndexOf($"\n{heading}\n", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing section '{heading}'.");
        var end = Guide.IndexOf("\n## ", start + heading.Length + 1, StringComparison.Ordinal);
        return end < 0 ? Guide[start..] : Guide[start..end];
    }

    private static string LocateRepoFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(BuildingAdaptersGuideTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not locate {relativePath} by walking up from the test assembly directory.");
    }
}

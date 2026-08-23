using System.Text.RegularExpressions;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class BicepTemplateProviderTests
{
    private static readonly Regex ModuleReference = new(
        @"^\s*module\s+\w+\s+'(?<path>[^']+)'",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void AssetsRoot_ExtractsBothEntryTemplates()
    {
        var context = new CommandContext(null);

        Assert.True(File.Exists(context.CoreBicepPath), $"missing {context.CoreBicepPath}");
        Assert.True(File.Exists(context.WebAppBicepPath), $"missing {context.WebAppBicepPath}");
    }

    /// <summary>
    /// The reason the extraction preserves directory structure: both entry templates
    /// reference their modules as 'templates/*.bicep' relative to themselves, so a
    /// flattened extraction would deploy nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryModuleReference_ResolvesRelativeToItsTemplate(bool webApp)
    {
        var context = new CommandContext(null);
        var entryTemplate = webApp ? context.WebAppBicepPath : context.CoreBicepPath;

        var references = ModuleReference.Matches(File.ReadAllText(entryTemplate))
            .Select(match => match.Groups["path"].Value)
            .Where(path => !path.StartsWith("br/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(references);

        var templateDirectory = Path.GetDirectoryName(entryTemplate)!;
        foreach (var reference in references)
        {
            var resolved = Path.GetFullPath(
                Path.Combine(templateDirectory, reference.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(resolved), $"'{reference}' referenced by {Path.GetFileName(entryTemplate)} did not resolve to {resolved}");
        }
    }

    [Fact]
    public void Extraction_IsIdempotentAndRepairsTruncatedFiles()
    {
        var context = new CommandContext(null);
        var target = context.CoreBicepPath;
        var expected = File.ReadAllText(target);

        // Simulate a run interrupted mid-write: without an unconditional overwrite the
        // truncated file would persist for the lifetime of the version-keyed directory.
        File.WriteAllText(target, string.Empty);

        var restored = File.ReadAllText(ReExtract().CoreBicepPath);

        Assert.Equal(expected, restored);
    }

    /// <summary>
    /// Extraction is cached per process, so re-running it means invoking the private
    /// extraction path again through a fresh context after the cache has been populated.
    /// </summary>
    private static CommandContext ReExtract()
    {
        typeof(BicepTemplateProvider)
            .GetMethod("Extract", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);
        return new CommandContext(null);
    }

    [Fact]
    public void ResolveVersion_StripsSourceRevisionSuffix()
    {
        var version = BicepTemplateProvider.ResolveVersion();

        Assert.DoesNotContain('+', version);
        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Tests that read the Bicep templates extracted from the CLI assembly. They run in one
/// collection, never in parallel, because
/// <see cref="BicepTemplateProviderTests.Extraction_IsIdempotentAndRepairsTruncatedFiles"/>
/// empties an extracted template on purpose and a parallel reader would see it empty.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ExtractedTemplatesCollection
{
    public const string Name = "Extracted Bicep templates";
}

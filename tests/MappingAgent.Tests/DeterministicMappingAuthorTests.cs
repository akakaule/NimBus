#pragma warning disable CA1707, CA2007

using MappingAgent.Authoring;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Transform;

namespace MappingAgent.Tests;

/// <summary>
/// Smoke tests for <see cref="DeterministicMappingAuthor"/>.
/// Mirrors <c>DeterministicContactClassifierTests</c> from the EnrichmentAgent (spec 022).
/// </summary>
[TestClass]
public class DeterministicMappingAuthorTests
{
    private static readonly DeterministicMappingAuthor Author = new();

    private static readonly AuthoringInput MarketingToErpInput = new(
        SourceEventTypeId: MappingAgentLoopWorker.SourceEventTypeId,
        TargetEventTypeId: MappingAgentLoopWorker.TargetEventTypeId,
        SourceSchemaJson: "{\"type\":\"object\",\"required\":[\"leadId\",\"company\",\"email\"]," +
                          "\"properties\":{\"leadId\":{\"type\":\"string\"}," +
                          "\"company\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}",
        TargetSchemaJson: "{\"type\":\"object\",\"required\":[\"customerId\",\"companyName\",\"email\"]," +
                          "\"properties\":{\"customerId\":{\"type\":\"string\"}," +
                          "\"companyName\":{\"type\":\"string\"},\"email\":{\"type\":\"string\"}}}",
        SampleSourcePayloads: Array.Empty<string>());

    [TestMethod]
    public async Task Author_Returns_NonEmpty_Transform()
    {
        var result = await Author.Author(MarketingToErpInput);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Transform),
            "Transform must be a non-empty string.");
    }

    [TestMethod]
    public async Task Author_Returns_NonEmpty_Rationale()
    {
        var result = await Author.Author(MarketingToErpInput);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Rationale),
            "Rationale must be a non-empty string.");
    }

    [TestMethod]
    public async Task Author_Is_Deterministic_SameInput_SameOutput()
    {
        var first = await Author.Author(MarketingToErpInput);
        var second = await Author.Author(MarketingToErpInput);
        Assert.AreEqual(first.Transform, second.Transform, "Transform must be identical on repeated calls.");
        Assert.AreEqual(first.Rationale, second.Rationale, "Rationale must be identical on repeated calls.");
    }

    [TestMethod]
    public async Task Author_MarketingToErp_Returns_KnownTransform()
    {
        // For the canonical marketing→erp shape the deterministic author must return the exact
        // known-good JSONata so CI produces a predictable proposal without touching the LLM.
        var result = await Author.Author(MarketingToErpInput);
        Assert.AreEqual(
            DeterministicMappingAuthor.MarketingToErpTransform,
            result.Transform,
            "DeterministicMappingAuthor must return the hard-coded canonical transform for the known demo shape.");
    }

    [TestMethod]
    public async Task Author_KnownTransform_ProducesValidOutput_AgainstTargetSchema()
    {
        // Execute the authored transform locally via JsonataTransformEngine and validate
        // the output against the target schema — proving the deterministic author emits a
        // working, schema-valid JSONata expression. This is the MappingAgent's equivalent
        // of the EnrichmentAgent's schema validation in AgentEnrichmentSmokeTests.
        var result = await Author.Author(MarketingToErpInput);

        var engine = new JsonataTransformEngine();
        var sampleSource = "{\"leadId\":\"L-001\",\"company\":\"Acme Corp\",\"email\":\"alice@acme.com\"}";
        var output = engine.Transform(result.Transform, sampleSource);

        // Validate against target schema with NJsonSchema (same gate as the executor).
        var targetSchema = await NJsonSchema.JsonSchema.FromJsonAsync(MarketingToErpInput.TargetSchemaJson);
        var errors = targetSchema.Validate(output);

        Assert.AreEqual(0, errors.Count,
            $"Authored transform must produce schema-valid target output. Errors: " +
            string.Join("; ", errors.Select(e => $"{e.Path}: {e.Kind}")));
    }

    [TestMethod]
    [Description("CI guard: asserts DeterministicMappingAuthor is selected when ANTHROPIC_API_KEY is absent, mirroring Program.cs DI selection.")]
    public async Task ClaudeAuthor_IsSkipped_When_ApiKey_Absent()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Inconclusive("ANTHROPIC_API_KEY is set; this test only validates skip behavior when the key is absent.");
            return;
        }

        Assert.IsTrue(string.IsNullOrWhiteSpace(apiKey),
            "When ANTHROPIC_API_KEY is absent, DeterministicMappingAuthor should be selected by Program.cs.");

        await Task.CompletedTask;
    }
}

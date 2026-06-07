using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Newtonsoft.Json;
using System.Text.Json;

namespace MappingAgent.Authoring;

/// <summary>
/// Uses the Anthropic Claude API (claude-haiku-4-5) with structured JSON output to author
/// a JSONata transform from the source schema/samples to the target schema.
/// Requires the <c>ANTHROPIC_API_KEY</c> environment variable to be set.
/// Mirrors <c>ClaudeContactClassifier</c> from the EnrichmentAgent.
/// </summary>
public sealed class ClaudeMappingAuthor : IMappingAuthor, IDisposable
{
    /// <summary>The model used for authoring. Haiku 4.5 is cheap and fast.</summary>
    public const string ModelId = "claude-haiku-4-5";

    private static readonly Dictionary<string, JsonElement> ProposalSchema = BuildSchema();

    private readonly AnthropicClient _client;

    /// <summary>
    /// Initialises a new instance using the ANTHROPIC_API_KEY environment variable.
    /// </summary>
    public ClaudeMappingAuthor()
    {
        _client = new AnthropicClient();
    }

    /// <summary>
    /// Initialises a new instance with a custom <see cref="AnthropicClient"/>.
    /// Useful for testing or injecting custom options.
    /// </summary>
    public ClaudeMappingAuthor(AnthropicClient client)
    {
        _client = client;
    }

    /// <inheritdoc/>
    public async Task<MappingProposal> Author(AuthoringInput input, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(input);

        var response = await _client.Messages.Create(
            new MessageCreateParams
            {
                Model = Model.ClaudeHaiku4_5,
                MaxTokens = 2048,
                Messages =
                [
                    new MessageParam
                    {
                        Role = Role.User,
                        Content = new MessageParamContent(prompt),
                    }
                ],
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat { Schema = ProposalSchema },
                },
            },
            cancellationToken);

        var json = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("Claude returned no text block for the mapping authoring request.");

        var result = JsonConvert.DeserializeObject<AuthoringResponse>(json)
            ?? throw new InvalidOperationException($"Claude returned null deserialized result. Raw JSON: {json}");

        return new MappingProposal(
            Transform: result.Transform,
            Rationale: result.Rationale ?? string.Empty);
    }

    private static string BuildPrompt(AuthoringInput input)
    {
        var samplesSection = input.SampleSourcePayloads.Count > 0
            ? "Sample source payloads (first 3):\n" +
              string.Join("\n", input.SampleSourcePayloads.Take(3).Select((p, i) => $"  [{i + 1}] {p}"))
            : "No sample source payloads available — synthesize a plausible example from the source schema.";

        return "You are an integration mapping expert. Author a JSONata expression that transforms a\n" +
               "source event payload to a valid target event payload.\n\n" +
               $"Source event type: {input.SourceEventTypeId}\n" +
               $"Target event type: {input.TargetEventTypeId}\n\n" +
               $"Source JSON Schema:\n{input.SourceSchemaJson}\n\n" +
               $"Target JSON Schema:\n{input.TargetSchemaJson}\n\n" +
               $"{samplesSection}\n\n" +
               "Instructions:\n" +
               "1. Produce a JSONata expression that maps the source fields to ALL required target fields.\n" +
               "   The output must be valid against the target JSON Schema.\n" +
               "2. Prefer direct field mappings (e.g. { \"targetField\": sourceField }).\n" +
               "   Use string functions for type coercions or concatenations if needed.\n" +
               "3. Write a short rationale (2-3 sentences) explaining the field mappings.\n\n" +
               "Respond ONLY with valid JSON matching the required schema.";
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        const string schemaJson = """
            {
              "type": "object",
              "required": ["transform", "rationale"],
              "properties": {
                "transform":  { "type": "string" },
                "rationale":  { "type": "string" }
              }
            }
            """;

        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>Private DTO matching the structured output schema.</summary>
    private sealed class AuthoringResponse
    {
        [JsonProperty("transform")]
        public string Transform { get; set; } = string.Empty;

        [JsonProperty("rationale")]
        public string? Rationale { get; set; }
    }
}

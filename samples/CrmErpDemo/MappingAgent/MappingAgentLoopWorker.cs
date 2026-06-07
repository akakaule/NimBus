using MappingAgent.Authoring;
using MappingAgent.Bus;
using Newtonsoft.Json;
using NimBus.Core.Transform;
using NimBus.MessageStore.States;

namespace MappingAgent;

/// <summary>
/// The MappingAgent authoring loop. On each iteration:
/// <list type="number">
///   <item>Reads source and target schemas from <c>GET /api/agent/catalog</c>.</item>
///   <item>Pulls sample source payloads from <c>GET /api/messages/search</c>; synthesises one from
///         the schema when none are available.</item>
///   <item>Runs worked examples locally via <see cref="JsonataTransformEngine"/> to validate the transform.</item>
///   <item>Computes <c>sourceSchemaHash</c> via <see cref="SchemaHash.Of"/> for drift detection.</item>
///   <item>Submits the mapping proposal via <c>POST /api/agent/mappings</c> (Draft state).</item>
/// </list>
/// The loop runs once per <see cref="LoopIntervalSeconds"/> and then idles, waiting for an operator
/// to approve the Draft via the WebApp. A <see cref="DeterministicMappingAuthor"/> is used when
/// <c>ANTHROPIC_API_KEY</c> is absent so CI is always green.
///
/// Mirrors <see cref="AgentLoopWorker"/> from the EnrichmentAgent (spec 022).
/// </summary>
public sealed class MappingAgentLoopWorker : BackgroundService
{
    /// <summary>The source event type the agent maps FROM.</summary>
    public const string SourceEventTypeId = "marketing.lead.created.v1";

    /// <summary>The target event type the agent maps TO.</summary>
    public const string TargetEventTypeId = "erp.customer.upsert.v1";

    // How long to wait between authoring iterations.
    private const int LoopIntervalSeconds = 60;

    private readonly IMappingBusGateway _bus;
    private readonly IMappingAuthor _author;
    private readonly JsonataTransformEngine _engine;
    private readonly ILogger<MappingAgentLoopWorker> _logger;

    public MappingAgentLoopWorker(
        IMappingBusGateway bus,
        IMappingAuthor author,
        ILogger<MappingAgentLoopWorker> logger)
    {
        _bus = bus;
        _author = author;
        _engine = new JsonataTransformEngine();
        _logger = logger;
    }

    /// <summary>
    /// Runs one author cycle. Returns <c>true</c> if a mapping was proposed, <c>false</c> if the
    /// required schemas were not found in the catalog (agent should retry later). Public for
    /// unit-testability against a fake <see cref="IMappingBusGateway"/>.
    /// </summary>
    public async Task<bool> AuthorNextAsync(CancellationToken ct)
    {
        // ── 1. Load schemas from catalog ──────────────────────────────────────
        var catalog = await _bus.GetCatalogAsync(ct);

        var sourceEntry = catalog.FirstOrDefault(e => e.EventTypeId == SourceEventTypeId);
        var targetEntry = catalog.FirstOrDefault(e => e.EventTypeId == TargetEventTypeId);

        if (sourceEntry is null || targetEntry is null)
        {
            _logger.LogWarning(
                "Catalog missing required schemas: source={SourceFound}, target={TargetFound}. " +
                "Schemas must be seeded before the MappingAgent can author a mapping.",
                sourceEntry is not null,
                targetEntry is not null);
            return false;
        }

        _logger.LogInformation(
            "Found schemas for {SourceEventTypeId} and {TargetEventTypeId} in catalog.",
            SourceEventTypeId, TargetEventTypeId);

        // ── 2. Pull sample source payloads ────────────────────────────────────
        var samples = await _bus.GetSamplePayloadsAsync(SourceEventTypeId, maxCount: 3, ct);
        if (samples.Count == 0)
        {
            _logger.LogInformation(
                "No sample payloads for {SourceEventTypeId}; synthesising from schema.",
                SourceEventTypeId);
            // Synthesise a minimal valid example from known field names in the schema.
            samples = new[]
            {
                "{\"leadId\":\"L-SYNTH-001\",\"company\":\"Acme Corp\",\"email\":\"demo@acme.com\"}"
            };
        }

        // ── 3. Author the transform ───────────────────────────────────────────
        var authoringInput = new AuthoringInput(
            SourceEventTypeId: SourceEventTypeId,
            TargetEventTypeId: TargetEventTypeId,
            SourceSchemaJson: sourceEntry.JsonSchema,
            TargetSchemaJson: targetEntry.JsonSchema,
            SampleSourcePayloads: samples);

        var proposal = await _author.Author(authoringInput, ct);

        _logger.LogInformation(
            "Authored transform for {Source} → {Target}: {Rationale}",
            SourceEventTypeId, TargetEventTypeId, proposal.Rationale);

        // ── 4. Run worked examples locally ────────────────────────────────────
        var workedExamples = new List<object>();
        foreach (var sample in samples.Take(3))
        {
            string output;
            try
            {
                output = _engine.Transform(proposal.Transform, sample);
            }
            catch (MappingTransformException ex)
            {
                _logger.LogWarning(ex,
                    "Transform failed on sample — the authored transform may be invalid. " +
                    "Submitting anyway for operator review.");
                output = $"{{\"error\":\"{ex.Message}\"}}";
            }

            workedExamples.Add(new { source = sample, output });
        }

        var workedExamplesJson = JsonConvert.SerializeObject(workedExamples);

        // ── 5. Compute schema hash (drift detection) ──────────────────────────
        var sourceSchemaHash = SchemaHash.Of(sourceEntry.JsonSchema);

        // ── 6. Submit proposal ────────────────────────────────────────────────
        var mappingId = await _bus.ProposeMappingAsync(
            SourceEventTypeId,
            TargetEventTypeId,
            proposal.Transform,
            proposal.Rationale,
            sourceSchemaHash,
            workedExamplesJson,
            ct);

        _logger.LogInformation(
            "Proposed mapping {MappingId} (Draft). An operator must approve it via the WebApp before it is applied.",
            mappingId);

        return true;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MappingAgent started. Will author {Source} → {Target} mapping.",
            SourceEventTypeId, TargetEventTypeId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var proposed = await AuthorNextAsync(stoppingToken);
                if (proposed)
                {
                    // Proposed successfully — back off and wait for operator approval.
                    // In a real agent this would poll for approval and re-author if the schema drifts.
                    _logger.LogInformation(
                        "MappingAgent sleeping {Seconds}s before next authoring iteration.",
                        LoopIntervalSeconds);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MappingAgent loop iteration failed; backing off before retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(LoopIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("MappingAgent stopped.");
    }
}

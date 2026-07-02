using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// REST endpoints for AI-authored event mappings (spec 023).
/// Agents propose mappings; operators approve/reject/pause/resume them.
/// </summary>
public class MappingImplementation : IAgentMappingsApiController
{
    private readonly IEventMappingStore _mappings;
    private readonly IEventSchemaStore _schemas;
    private readonly ILogger<MappingImplementation> _logger;

    public MappingImplementation(
        IEventMappingStore mappings,
        IEventSchemaStore schemas,
        ILogger<MappingImplementation> logger)
    {
        _mappings = mappings;
        _schemas = schemas;
        _logger = logger;
    }

    // ── POST /api/agent/mappings ──────────────────────────────────────────────

    public async Task<ActionResult<MappingInfo>> PostAgentMappingsAsync(ProposeMappingRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.SourceEventTypeId) || string.IsNullOrWhiteSpace(body.TargetEventTypeId))
            return new BadRequestObjectResult("sourceEventTypeId and targetEventTypeId are required.");
        if (string.IsNullOrWhiteSpace(body.Transform))
            return new BadRequestObjectResult("transform is required.");
        if (await _schemas.GetSchema(body.SourceEventTypeId) is null || await _schemas.GetSchema(body.TargetEventTypeId) is null)
            return new NotFoundObjectResult("Both source and target event types must be registered.");

        var id = $"{body.SourceEventTypeId}->{body.TargetEventTypeId}";
        var existing = await _mappings.GetMapping(id);

        // Guard the approval gate: an operator-approved mapping (Active or Paused) must not be
        // silently demoted back to Draft by a fresh proposal. The operator has to pause/reject it
        // first. Re-proposing over an absent/Draft/Rejected/Stale record is allowed (re-author flow).
        if (existing is not null && (existing.State == MappingState.Active || existing.State == MappingState.Paused))
        {
            _logger.LogInformation(
                "Rejected re-proposal of {Id}: existing mapping is {State} (operator-approved).", id, existing.State);
            return new ConflictObjectResult(
                $"Mapping {id} is {existing.State} and operator-approved; it cannot be overwritten by a new proposal. " +
                "Reject it (or pause then reject an Active mapping) before re-proposing.");
        }

        var mapping = new EventMapping
        {
            Id = id,
            SourceEventTypeId = body.SourceEventTypeId,
            TargetEventTypeId = body.TargetEventTypeId,
            Transform = body.Transform,
            Rationale = body.Rationale,
            WorkedExamplesJson = body.WorkedExamplesJson,
            SourceSchemaHash = body.SourceSchemaHash,
            State = MappingState.Draft,
            Version = (existing?.Version ?? 0) + 1,
            CreatedBy = "demo-agent",
            CreatedUtc = DateTime.UtcNow,
        };
        var saved = await _mappings.SaveMapping(mapping);
        _logger.LogInformation("Mapping proposed: {Id} (version {Version})", saved.Id, saved.Version);
        return new OkObjectResult(ToInfo(saved));
    }

    // ── GET /api/agent/mappings ───────────────────────────────────────────────

    public async Task<ActionResult<IEnumerable<MappingInfo>>> GetAgentMappingsAsync()
        => new OkObjectResult((await _mappings.GetMappings()).Select(ToInfo).ToList());

    // ── POST /api/agent/mappings/{id}/approve ─────────────────────────────────

    public Task<IActionResult> PostAgentMappingApproveAsync(string id)
        => TransitionAsync(id, MappingState.Active, approve: true);

    // ── POST /api/agent/mappings/{id}/reject ──────────────────────────────────

    public Task<IActionResult> PostAgentMappingRejectAsync(string id)
        => TransitionAsync(id, MappingState.Rejected, approve: false,
            allowedFrom: new[] { MappingState.Draft, MappingState.Stale });

    // ── POST /api/agent/mappings/{id}/pause ───────────────────────────────────

    public Task<IActionResult> PostAgentMappingPauseAsync(string id)
        => TransitionAsync(id, MappingState.Paused, approve: false,
            allowedFrom: new[] { MappingState.Active });

    // ── POST /api/agent/mappings/{id}/resume ──────────────────────────────────

    public Task<IActionResult> PostAgentMappingResumeAsync(string id)
        => TransitionAsync(id, MappingState.Active, approve: false,
            allowedFrom: new[] { MappingState.Paused });

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions a mapping to <paramref name="target"/>. When <paramref name="allowedFrom"/> is
    /// non-null the current state must be one of the listed states, otherwise the call is a 409 —
    /// this keeps the approval gate intact (e.g. resume is Paused→Active only, so a Draft can never
    /// be activated without approval; pause is Active-only so a Draft→Paused→Resume path can't
    /// smuggle an unapproved mapping into Active). The approve endpoint passes no guard so its
    /// existing semantics are preserved.
    /// </summary>
    private async Task<IActionResult> TransitionAsync(
        string id, MappingState target, bool approve, MappingState[]? allowedFrom = null)
    {
        var mapping = await _mappings.GetMapping(id);
        if (mapping is null)
            return new NotFoundObjectResult("Unknown mapping.");

        if (allowedFrom is not null && Array.IndexOf(allowedFrom, mapping.State) < 0)
        {
            _logger.LogInformation(
                "Rejected transition of {Id} to {Target}: current state {State} not in {Allowed}.",
                id, target, mapping.State, string.Join("/", allowedFrom));
            return new ConflictObjectResult(
                $"Mapping {id} is {mapping.State}; this operation requires it to be {string.Join(" or ", allowedFrom)}.");
        }

        if (approve)
        {
            var schema = await _schemas.GetSchema(mapping.SourceEventTypeId);
            if (schema is null || SchemaHash.Of(schema.JsonSchema) != mapping.SourceSchemaHash)
            {
                mapping.State = MappingState.Stale;
                await _mappings.SaveMapping(mapping);
                _logger.LogWarning("Mapping {Id} marked Stale: source schema drifted", id);
                return new ConflictObjectResult("Source schema drifted; mapping marked Stale. Re-author required.");
            }
            mapping.ApprovedBy = "operator";
            mapping.ApprovedUtc = DateTime.UtcNow;
        }

        mapping.State = target;
        await _mappings.SaveMapping(mapping);
        _logger.LogInformation("Mapping {Id} transitioned to {State}", id, target);
        return new OkResult();
    }

    private static MappingInfo ToInfo(EventMapping m) => new MappingInfo
    {
        Id = m.Id,
        SourceEventTypeId = m.SourceEventTypeId,
        TargetEventTypeId = m.TargetEventTypeId,
        Transform = m.Transform,
        Rationale = m.Rationale,
        WorkedExamplesJson = m.WorkedExamplesJson,
        State = Enum.Parse<MappingInfoState>(m.State.ToString()),
        Version = m.Version,
        CreatedBy = m.CreatedBy,
        ApprovedBy = m.ApprovedBy,
    };
}

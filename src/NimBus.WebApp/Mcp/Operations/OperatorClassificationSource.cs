using NimBus.Extensions.IntegrationIntelligence;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>Reads stored AI failure classifications for the operator tools.</summary>
public interface IOperatorClassificationSource
{
    /// <summary>Whether failure classification is enabled and ready in this deployment.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The latest classification of one failed attempt, or null when there is none or the
    /// caller cannot read it. The classification service applies its own endpoint Reader check.
    /// </summary>
    Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Requests or reuses an AI classification of one failed attempt through the classification
    /// service, which checks Contributor, eligibility and the idempotency key and audits the
    /// request. Returns the classification and whether a stored one was reused.
    /// </summary>
    Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(string endpointId, string eventId, string messageId,
        string idempotencyKey, bool force, CancellationToken cancellationToken);
}

/// <summary>
/// Reads through <see cref="FailureClassificationService"/>, which is registered only when the
/// feature is enabled and ready. Reading never starts an analysis or calls a provider.
/// </summary>
public sealed class OperatorClassificationSource : IOperatorClassificationSource
{
    private readonly FailureClassificationService? _service;

    /// <summary>Creates the source; <paramref name="services"/> is empty when the feature is off.</summary>
    public OperatorClassificationSource(IEnumerable<FailureClassificationService> services)
        => _service = services.FirstOrDefault();

    /// <inheritdoc />
    public bool IsAvailable => _service is not null;

    /// <inheritdoc />
    public async Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
    {
        if (_service is null)
            return null;

        try
        {
            return await _service.GetLatestAsync(eventId, messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception) when (exception.StatusCode is 403 or 404)
        {
            // Not readable or gone: indistinguishable from "not classified" by design.
            return null;
        }
        catch (ClassificationServiceException exception)
        {
            throw OperatorToolErrors.SourceUnavailable($"Failure classification is unavailable ({exception.Code}).");
        }
    }

    /// <inheritdoc />
    public async Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(string endpointId, string eventId, string messageId,
        string idempotencyKey, bool force, CancellationToken cancellationToken)
    {
        if (_service is null)
            throw OperatorToolErrors.FeatureUnavailable("AI failure classification is not enabled in this deployment.");

        try
        {
            return await _service.AnalyzeAsync(eventId, messageId, idempotencyKey, force, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception)
        {
            throw exception.Code switch
            {
                "FailureNotFound" or "FailureDeleted" => OperatorToolErrors.MessageNotFound(endpointId, eventId),
                "Forbidden" or "EndpointNotAllowed" => OperatorToolErrors.PermissionDenied(
                    "Classifying a failure requires the Contributor role on the endpoint."),
                "FailureNotEligible" => OperatorToolErrors.ActionNotAllowed(
                    "Only a Failed or DeadLettered message's error can be classified."),
                "AnalysisInProgress" => OperatorToolErrors.ActionNotAllowed(
                    "A classification of this failure is already running. Read it with nimbus_get_classification shortly."),
                "InvalidIdempotencyKey" or "IdempotencyConflict" => OperatorToolErrors.InvalidArgument(
                    "idempotencyKey must be a new GUID, or the one you used before with the same force value."),
                "AnalysisOutcomeUnknown" => OperatorToolErrors.OutcomeUnknown("The classification may or may not have been stored."),
                _ => OperatorToolErrors.SourceUnavailable($"Failure classification is unavailable ({exception.Code})."),
            };
        }
    }
}

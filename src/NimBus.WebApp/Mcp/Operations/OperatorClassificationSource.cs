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
}

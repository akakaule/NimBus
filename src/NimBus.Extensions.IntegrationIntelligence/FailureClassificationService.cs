using System.Diagnostics;
using System.Text.Json;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Providers;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Coordinates authorization, evidence, provider calls and durable completion.</summary>
public sealed class FailureClassificationService
{
    private readonly FailureClassificationOptions _options;
    private readonly IIntegrationIntelligenceHost _host;
    private readonly IFailureClassificationStore _store;
    private readonly FailureEvidenceBuilder _evidence;
    private readonly IFailureIntelligenceProvider _provider;
    private readonly TimeProvider _clock;
    private readonly IMessageTrackingStore _messages;

    /// <summary>Creates the service.</summary>
    public FailureClassificationService(
        FailureClassificationOptions options,
        IIntegrationIntelligenceHost host,
        IFailureClassificationStore store,
        FailureEvidenceBuilder evidence,
        IFailureIntelligenceProvider provider,
        IMessageTrackingStore messages,
        TimeProvider? clock = null)
    {
        _options = options;
        _host = host;
        _store = store;
        _evidence = evidence;
        _provider = provider;
        _messages = messages;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Loads the latest authorized classification.</summary>
    public async Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
    {
        var input = await LoadAuthorizedMessageAsync(eventId, messageId, requireContributor: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        var operation = await _store.GetLatestOperationAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
        if (operation is { Status: "Active" }) throw new ClassificationServiceException("AnalysisInProgress", 409);
        if (operation is { Status: "Failed", ErrorCode: "AnalysisOutcomeUnknown" }) throw new ClassificationServiceException("AnalysisOutcomeUnknown", 409);
        if (operation is { Status: "Failed" }) throw new ClassificationServiceException(operation.ErrorCode ?? "AnalysisFailed", 503);
        return await _store.GetLatestAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads all authorized revisions.</summary>
    public async Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string eventId, string messageId, CancellationToken cancellationToken)
    {
        var input = await LoadAuthorizedMessageAsync(eventId, messageId, requireContributor: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _store.GetHistoryAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one idempotent analysis request.</summary>
    public async Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(
        string eventId, string messageId, string idempotencyKey, bool force, CancellationToken cancellationToken)
    {
        FailureClassification? result = null;
        var cached = false;
        var outcome = "failed";
        var denied = false;
        var endpointId = string.Empty;
        try
        {
            await LoadAuthorizedMessageAsync(eventId, messageId, true, endpoint => endpointId = endpoint, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(idempotencyKey, out var key)) throw new ClassificationServiceException("InvalidIdempotencyKey", 400);
            idempotencyKey = key.ToString("D");
            (result, cached) = await AnalyzeCoreAsync(eventId, messageId, idempotencyKey, force, cancellationToken).ConfigureAwait(false);
            outcome = cached ? "cached" : "completed";
            return (result, cached);
        }
        catch (ClassificationServiceException exception)
        {
            outcome = exception.Code;
            denied = exception.StatusCode == 403;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new ClassificationServiceException("ClassificationUnavailable", 503, inner: exception);
        }
        finally
        {
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AuditAsync(eventId, endpointId, messageId, result, cached, outcome, denied, auditTimeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<(FailureClassification Result, bool Cached)> AnalyzeCoreAsync(
        string eventId,
        string messageId,
        string idempotencyKey,
        bool force,
        CancellationToken cancellationToken)
    {
        using var activity = NimBusIntelligenceTelemetry.ActivitySource.StartActivity("NimBus.Intelligence.FailureClassification");
        var started = Stopwatch.GetTimestamp();
        NimBusIntelligenceTelemetry.Requests.Add(1, new KeyValuePair<string, object?>("nimbus.intelligence.provider", _provider.Name));
        var input = await _evidence.BuildAsync(eventId, messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new ClassificationServiceException("FailureNotEligible", 409);
        if (_options.AllowedEndpoints.Length > 0 && !_options.AllowedEndpoints.Contains(input.EndpointId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ClassificationServiceException("EndpointNotAllowed", 403);
        }

        var reservation = await _store.ReserveAsync(input.MessageId, idempotencyKey, force, _clock.GetUtcNow(),
            new ClassificationScope(input.MessageId, eventId, input.EndpointId, input.SessionId), cancellationToken).ConfigureAwait(false);
        if (reservation.CachedResult is not null)
        {
            RecordDuration(started, "cached", reservation.CachedResult.Category, reservation.CachedResult.Model);
            return (reservation.CachedResult, true);
        }

        if (!reservation.IsOwner)
        {
            var code = reservation.ErrorCode ?? "AnalysisInProgress";
            throw new ClassificationServiceException(code, code is "AnalysisInProgress" or "AnalysisOutcomeUnknown" ? 409 : 503);
        }

        FailureIntelligenceProviderResult providerResult;
        try
        {
            providerResult = await _provider.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (IntelligenceProviderException exception)
        {
            var errorCode = exception.OutcomeUnknown ? "AnalysisOutcomeUnknown" : exception.Code;
            await MarkFailedAsync(reservation, errorCode).ConfigureAwait(false);
            NimBusIntelligenceTelemetry.Errors.Add(1, new KeyValuePair<string, object?>("nimbus.intelligence.provider", _provider.Name));
            RecordDuration(started, "provider_error", null, _options.Model);
            throw new ClassificationServiceException(errorCode, 503, inner: exception);
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(reservation, "AnalysisOutcomeUnknown").ConfigureAwait(false);
            NimBusIntelligenceTelemetry.Errors.Add(1, new KeyValuePair<string, object?>("nimbus.intelligence.provider", _provider.Name));
            RecordDuration(started, "provider_error", null, _options.Model);
            throw new ClassificationServiceException("AnalysisOutcomeUnknown", 503, inner: exception);
        }

        var result = new FailureClassification
        {
            Id = $"{input.MessageId}:{reservation.Revision}",
            FailureMessageId = input.MessageId,
            Revision = reservation.Revision,
            EventId = input.EventId,
            EventTypeId = input.EventTypeId,
            EndpointId = input.EndpointId,
            SessionId = input.SessionId,
            Provider = _provider.Name,
            Model = providerResult.Model,
            QuestionSetVersion = FailureClassificationQuestionSet.Version,
            Category = providerResult.Category,
            CategoryConfidence = providerResult.CategoryConfidence,
            CategoryProbabilities = providerResult.CategoryProbabilities,
            RetryLikelihood = providerResult.RetryLikelihood,
            ChangeRequiredLikelihood = providerResult.ChangeRequiredLikelihood,
            ExternalDependencyLikelihood = providerResult.ExternalDependencyLikelihood,
            Guidance = FailureGuidanceRules.Compose(providerResult, _options),
            InputTokens = providerResult.InputTokens,
            OutputTokens = providerResult.OutputTokens,
            EventPayloadIncluded = input.EventPayloadJson is not null,
            RequestedBy = _host.CurrentActor ?? "unknown",
            CreatedAtUtc = _clock.GetUtcNow(),
        };

        if (await _messages.GetEvent(input.EndpointId, eventId).ConfigureAwait(false) is null)
        {
            if (_store is IClassificationRetentionStore retention)
                await retention.DeleteFailureAsync(messageId, cancellationToken).ConfigureAwait(false);
            throw new ClassificationServiceException("FailureNotFound", 404);
        }
        try
        {
            await _store.CompleteAsync(reservation.OperationId, result, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException) { throw; }
        catch (Exception exception)
        {
            // A lost acknowledgement must not trigger another paid call or overwrite completion.
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var saved = await _store.GetLatestOperationAsync(messageId, readTimeout.Token).ConfigureAwait(false);
                if (saved?.OperationId == reservation.OperationId && saved.Result is not null) return (saved.Result, false);
            }
            catch { /* Leave the durable reservation unknown if storage remains unavailable. */ }
            await MarkFailedAsync(reservation, "AnalysisOutcomeUnknown").ConfigureAwait(false);
            throw new ClassificationServiceException("AnalysisOutcomeUnknown", 503, inner: exception);
        }
        if (result.InputTokens is { } tokens) NimBusIntelligenceTelemetry.InputTokens.Add(tokens, new KeyValuePair<string, object?>("nimbus.intelligence.provider", result.Provider));
        RecordDuration(started, "ok", result.Category, result.Model);
        return (result, false);
    }

    private static void RecordDuration(long started, string outcome, string? category, string model)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        NimBusIntelligenceTelemetry.Duration.Record(elapsed,
            new KeyValuePair<string, object?>("nimbus.intelligence.outcome", outcome),
            new KeyValuePair<string, object?>("nimbus.intelligence.category", category),
            new KeyValuePair<string, object?>("nimbus.intelligence.model", model));
    }

    private async Task MarkFailedAsync(ClassificationReservation reservation, string code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _store.FailAsync(reservation, code, timeout.Token).ConfigureAwait(false); }
        catch { /* Expiry is an unknown outcome, never permission to replay. */ }
    }

    private async Task<MessageEntity> LoadAuthorizedMessageAsync(string eventId, string messageId, bool requireContributor, Action<string>? loadedEndpoint = null, CancellationToken cancellationToken = default)
    {
        var input = await _messages.GetMessage(eventId, messageId).ConfigureAwait(false);
        if (input is null)
        {
            throw new ClassificationServiceException("FailureNotFound", 404);
        }
        loadedEndpoint?.Invoke(input.EndpointId);

        if (!await _host.EndpointExistsAsync(input.EndpointId, cancellationToken).ConfigureAwait(false)
            || await _messages.GetEvent(input.EndpointId, eventId).ConfigureAwait(false) is null)
        {
            throw new ClassificationServiceException("FailureNotFound", 404);
        }

        var authorized = requireContributor
            ? await _host.HasContributorAsync(input.EndpointId, cancellationToken).ConfigureAwait(false)
            : await _host.HasReaderAsync(input.EndpointId, cancellationToken).ConfigureAwait(false);
        if (!authorized)
        {
            throw new ClassificationServiceException("Forbidden", 403);
        }

        return input;
    }

    private async Task AuditAsync(string eventId, string endpointId, string messageId, FailureClassification? result, bool cached, string outcome, bool accessDenied, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            messageId,
            provider = result?.Provider,
            model = result?.Model,
            questionSetVersion = result?.QuestionSetVersion,
            category = result?.Category,
            categoryConfidence = result?.CategoryConfidence,
            guidance = result?.Guidance.ToString(),
            inputTokens = result?.InputTokens,
            eventPayloadIncluded = result?.EventPayloadIncluded,
            revision = result?.Revision,
            cached,
            outcome,
        });
        try
        {
            await _host.AuditAsync(MessageStore.MessageAuditType.FailureClassified, eventId, endpointId, data, accessDenied, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit is deliberately best-effort and must never replay a paid request.
        }
    }
}

/// <summary>Machine-readable service failure returned by the API layer.</summary>
public sealed class ClassificationServiceException : Exception
{
    /// <summary>Creates a service exception.</summary>
    public ClassificationServiceException(string code, int statusCode, string? message = null, Exception? inner = null)
        : base(message ?? code, inner)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>Error code.</summary>
    public string Code { get; }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }
}

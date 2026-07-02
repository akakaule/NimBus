using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace NimBus.Agents;

/// <summary>
/// The declarative outcome an <see cref="IAgentHandler{TInput}"/> returns: zero or more events
/// to publish, then a settle decision (complete or fail). The SDK owns the ordering — it
/// publishes every <see cref="PublishSpec"/> first and settles exactly once, and never settles
/// if a publish throws.
/// </summary>
public sealed class AgentResult
{
    private AgentResult(IReadOnlyList<PublishSpec> publishes, bool isSuccess, string? result, string? errorText, string? errorType)
    {
        Publishes = publishes;
        IsSuccess = isSuccess;
        Result = result;
        ErrorText = errorText;
        ErrorType = errorType;
    }

    /// <summary>Events to publish, in order, before settling.</summary>
    public IReadOnlyList<PublishSpec> Publishes { get; }

    /// <summary><c>true</c> to settle the handoff "complete"; <c>false</c> to settle "fail".</summary>
    public bool IsSuccess { get; }

    /// <summary>Optional result note recorded on a successful settle.</summary>
    public string? Result { get; }

    /// <summary>Error text recorded on a failed settle (the API requires it when failing).</summary>
    public string? ErrorText { get; }

    /// <summary>Optional error-type label recorded on a failed settle.</summary>
    public string? ErrorType { get; }

    /// <summary>Publish the given events, then settle the handoff complete.</summary>
    public static AgentResult Complete(params PublishSpec[] publishes) =>
        new(publishes ?? Array.Empty<PublishSpec>(), isSuccess: true, result: null, errorText: null, errorType: null);

    /// <summary>Settle the handoff complete with no published events.</summary>
    public static AgentResult Done(string? result = null) =>
        new(Array.Empty<PublishSpec>(), isSuccess: true, result: result, errorText: null, errorType: null);

    /// <summary>Settle the handoff fail. Nothing is published. <paramref name="errorText"/> is required.</summary>
    public static AgentResult Fail(string errorText, string? errorType = null)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            throw new ArgumentException("errorText is required when failing a handoff.", nameof(errorText));
        return new(Array.Empty<PublishSpec>(), isSuccess: false, result: null, errorText: errorText, errorType: errorType);
    }
}

/// <summary>One event to publish. A null <see cref="SessionId"/> inherits the received handoff's session.</summary>
/// <param name="EventTypeId">The event type id to publish.</param>
/// <param name="Payload">The event body as a JSON string (validated against the type's schema server-side).</param>
/// <param name="SessionId">Session to publish on; null inherits the received event's session.</param>
public sealed record PublishSpec(string EventTypeId, string Payload, string? SessionId = null)
{
    private static readonly JsonSerializerSettings CamelCase = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    /// <summary>Serialize <paramref name="payload"/> to camelCase JSON and wrap it as a <see cref="PublishSpec"/>.</summary>
    /// <param name="eventTypeId">The event type id to publish.</param>
    /// <param name="payload">The payload object; serialized with camelCase property names.</param>
    /// <param name="sessionId">Session to publish on; null inherits the received event's session.</param>
    public static PublishSpec FromObject(string eventTypeId, object payload, string? sessionId = null) =>
        new(eventTypeId, JsonConvert.SerializeObject(payload, CamelCase), sessionId);
}

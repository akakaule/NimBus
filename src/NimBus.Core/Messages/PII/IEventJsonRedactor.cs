namespace NimBus.Core.Messages.PII;

/// <summary>
/// Fully redacts sensitive leaves from a serialized event payload.
/// </summary>
public interface IEventJsonRedactor
{
    /// <summary>
    /// Returns a fail-closed payload in which every sensitive leaf is replaced
    /// with the redaction token, regardless of its configured mask mode.
    /// </summary>
    string Redact(string eventTypeId, string eventJson);
}

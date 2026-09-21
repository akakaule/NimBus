namespace NimBus.Core.Messages.PII;

/// <summary>
/// Marker implementation used when a host supplies a custom masker without
/// the optional full-redaction capability.
/// </summary>
public sealed class NullEventJsonRedactor : IEventJsonRedactor
{
    /// <inheritdoc />
    public string Redact(string eventTypeId, string eventJson) => EventJsonMasker.UnknownTypeMarker;
}

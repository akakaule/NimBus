using System.Collections.Generic;
using NimBus.Core.Messages.PII;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <summary>
/// Field-level payload masking for non-PiiReader responses. Only values annotated
/// <see cref="SensitiveAttribute"/> on the event class are masked; every other field
/// stays readable so operators can still triage a failure without PII access.
/// Applied server-side at DTO-mapping time so raw sensitive values never cross the
/// wire — the message store keeps the real payload; this is a response boundary
/// transform only.
/// </summary>
/// <remarks>
/// Fails closed: <see cref="IEventJsonMasker.Mask"/> returns a marker string rather
/// than the original payload when the event type cannot be resolved to a CLR type
/// (dynamically-typed events) or the JSON does not parse, so an unrecognised shape
/// is never emitted unmasked.
/// </remarks>
public class PayloadRedaction
{
    private readonly IEventJsonMasker _masker;

    public PayloadRedaction(IEventJsonMasker masker) => _masker = masker ?? NullEventJsonMasker.Instance;

    public Event? Redact(Event? e)
    {
        var content = e?.MessageContent?.EventContent;
        if (content != null && !string.IsNullOrEmpty(content.EventJson))
            content.EventJson = _masker.Mask(content.EventTypeId, content.EventJson);
        return e;
    }

    public IEnumerable<Event> Redact(IEnumerable<Event> events)
    {
        foreach (var e in events)
            Redact(e);
        return events;
    }

    public EndpointStatus? Redact(EndpointStatus? status)
    {
        if (status?.EnrichedUnresolvedEvents != null)
        {
            foreach (var e in status.EnrichedUnresolvedEvents)
                Redact(e);
        }

        return status;
    }

    public Message? Redact(Message? m)
    {
        if (m != null && !string.IsNullOrEmpty(m.EventContent))
            m.EventContent = _masker.Mask(m.EventTypeId, m.EventContent);
        return m;
    }

    public EventDetails? Redact(EventDetails? details)
    {
        Redact(details?.FailedMessage);
        Redact(details?.OriginatingMessage);
        return details;
    }

    public EventLogEntry Redact(EventLogEntry log)
    {
        if (!string.IsNullOrEmpty(log.Payload))
            log.Payload = _masker.Mask(log.EventType, log.Payload);
        return log;
    }

    public static EndpointSubscription RedactSubscription(EndpointSubscription subscription)
    {
        // Subscription payload filters are operator-authored payload fragments —
        // omit rather than mask them (the field is a filter, not a document, so
        // there is no event type to resolve annotations against).
        subscription.Payload = null;
        return subscription;
    }
}

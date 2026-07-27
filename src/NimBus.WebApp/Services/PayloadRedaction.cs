using System.Collections.Generic;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <summary>
/// Whole-payload redaction for non-PiiReader responses (spec 026; refined to
/// field-level [Sensitive] masking by spec 021 later). Applied server-side at
/// DTO-mapping time so raw payloads never cross the wire — the SPA renders the
/// placeholder with a lock affordance. The message store keeps the real
/// payload; this is a response boundary transform only.
/// </summary>
public static class PayloadRedaction
{
    public const string Placeholder = "[REDACTED]";

    public static Event? Redact(Event? e)
    {
        var content = e?.MessageContent?.EventContent;
        if (content != null && !string.IsNullOrEmpty(content.EventJson))
            content.EventJson = Placeholder;
        return e;
    }

    public static IEnumerable<Event> Redact(IEnumerable<Event> events)
    {
        foreach (var e in events)
            Redact(e);
        return events;
    }

    public static EndpointStatus? Redact(EndpointStatus? status)
    {
        if (status?.EnrichedUnresolvedEvents != null)
        {
            foreach (var e in status.EnrichedUnresolvedEvents)
                Redact(e);
        }

        return status;
    }

    public static Message? Redact(Message? m)
    {
        if (m != null && !string.IsNullOrEmpty(m.EventContent))
            m.EventContent = Placeholder;
        return m;
    }

    public static EventDetails? Redact(EventDetails? details)
    {
        Redact(details?.FailedMessage);
        Redact(details?.OriginatingMessage);
        return details;
    }

    public static EventLogEntry Redact(EventLogEntry log)
    {
        if (!string.IsNullOrEmpty(log.Payload))
            log.Payload = Placeholder;
        return log;
    }

    public static EndpointSubscription RedactSubscription(EndpointSubscription subscription)
    {
        // Subscription payload filters are operator-authored payload fragments —
        // omit rather than placeholder them (the field is a filter, not a doc).
        subscription.Payload = null;
        return subscription;
    }
}

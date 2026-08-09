using System.Collections.Generic;

namespace NimBus.Core.Messages.PII
{
    /// <summary>
    /// Masks <see cref="SensitiveAttribute"/>-annotated values inside a serialized event
    /// payload, leaving every non-sensitive field readable.
    /// </summary>
    public interface IEventJsonMasker
    {
        /// <summary>
        /// Returns <paramref name="eventJson"/> with every sensitive leaf masked. Fails closed:
        /// an unresolvable event type or invalid JSON yields a marker string rather than the
        /// original payload, so an unknown shape can never leak.
        /// </summary>
        string Mask(string eventTypeId, string eventJson);

        /// <summary>
        /// Returns true if the given JSON appears to contain previously-masked sensitive content.
        /// Detection is primarily via the <see cref="EventJsonMasker.PiiMaskedMarker"/> sidecar
        /// property at the JSON root, with a fallback per-field scan for the
        /// <see cref="EventJsonMasker.DefaultRedactToken"/> in any property marked
        /// <see cref="SensitiveAttribute"/>. Used to detect operators resubmitting a masked
        /// payload without re-entering PII.
        /// </summary>
        bool ContainsRedactPlaceholder(string eventTypeId, string eventJson);

        /// <summary>
        /// Removes the <see cref="EventJsonMasker.PiiMaskedMarker"/> sidecar property from the
        /// JSON root if present. Call before forwarding operator-supplied JSON to a downstream
        /// system that should not see the marker.
        /// </summary>
        string StripMaskedMarker(string eventJson);

        /// <summary>
        /// Collects the raw values of every <see cref="SensitiveAttribute"/>-annotated leaf in the
        /// payload, so callers can scrub those values out of free-form text that may quote them
        /// (error messages, stack traces, broker dead-letter descriptions). Returns false when a
        /// non-empty payload cannot be analyzed (unknown event type or invalid JSON) — the caller
        /// must then fail closed and withhold the text. An empty payload yields true with an empty
        /// collection.
        /// </summary>
        bool TryCollectSensitiveValues(string eventTypeId, string eventJson, out IReadOnlyCollection<string> values);
    }
}

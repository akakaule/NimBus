using System;
using System.Collections.Generic;

namespace NimBus.Core.Messages.PII
{
    /// <summary>
    /// No-op masker used when no platform context is available (sample apps, bootstrap logging).
    /// Returns the input unchanged. Production hosts MUST register <see cref="EventJsonMasker"/> instead.
    /// </summary>
    public sealed class NullEventJsonMasker : IEventJsonMasker
    {
        public static readonly NullEventJsonMasker Instance = new NullEventJsonMasker();

        public string Mask(string eventTypeId, string eventJson) => eventJson;

        public bool ContainsRedactPlaceholder(string eventTypeId, string eventJson) => false;

        public string StripMaskedMarker(string eventJson) => eventJson;

        public bool TryCollectSensitiveValues(string eventTypeId, string eventJson, out IReadOnlyCollection<string> values)
        {
            values = Array.Empty<string>();
            return true;
        }
    }
}

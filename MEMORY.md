# Memory

- AsyncAPI governance corrections: validate operation `messages` refs by accepting both generated channel-scoped refs and direct `#/components/messages/...` refs; diff enum sets recursively through array `items` so removed item enum values stay breaking.
- AsyncAPI exporter examples: convert object-map payloads (`JObject`, `IDictionary`, and `IReadOnlyDictionary`) before the generic `IEnumerable` fallback so fluent examples export as JSON objects instead of arrays of entries.

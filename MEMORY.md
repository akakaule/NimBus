# Memory

- AsyncAPI governance corrections: validate operation `messages` refs by accepting both generated channel-scoped refs and direct `#/components/messages/...` refs; diff enum sets recursively through array `items` so removed item enum values stay breaking.
- AsyncAPI exporter examples: convert object-map payloads (`JObject`, `IDictionary`, and `IReadOnlyDictionary`) before the generic `IEnumerable` fallback so fluent examples export as JSON objects instead of arrays of entries.
- AsyncAPI merge conflict pattern: when exporter code moves to `NimBus.ServiceBus.AsyncApi`, keep `NimBus.Core.Events.AsyncApiFormat` as the canonical SDK/provider type and use distinct aliases (`CoreAsyncApiFormat`, `ServiceBusAsyncApiExporter`) inside `NimBus.CommandLine` to avoid obsolete bridge type shadowing.

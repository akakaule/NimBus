using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace NimBus.CommandLine;

/// <summary>Output format for <see cref="AsyncApiExporter"/>.</summary>
public enum AsyncApiFormat
{
    /// <summary>AsyncAPI 3.0 as YAML (default).</summary>
    Yaml,

    /// <summary>AsyncAPI 3.0 as JSON.</summary>
    Json,
}

/// <summary>
/// Generates an AsyncAPI 3.0 document from an <see cref="IPlatform"/>.
/// <para>
/// The document is a <em>hybrid</em> view: portable logical channels/operations for developer
/// portals, enriched with Azure Service Bus specifics (the auto-forward hops, subscription rules,
/// session/dead-letter settings and <c>user.*</c> application-property conventions) carried via
/// AsyncAPI specification extensions (<c>x-servicebus*</c> / <c>x-nimbus*</c>) because no official
/// AsyncAPI Service Bus binding exists. This bridges the logical event contract and NimBus's real
/// topic-per-endpoint topology.
/// </para>
/// </summary>
public static class AsyncApiExporter
{
    // Kept in lock-step with ServiceBusTopologyProvisioner so the documented rules match what
    // provisioning actually creates. If those SQL expressions change, change them here too.
    private static string ForwardFilter(string eventTypeId) =>
        $"user.EventTypeId = '{eventTypeId}' AND user.From IS NULL";

    private static string ForwardAction(string from, string to) =>
        $"SET user.From = '{from}'; SET user.EventId = newid(); SET user.To = '{to}';";

    private static string DeliveryFilter(string endpointId) => $"user.To = '{endpointId}'";

    /// <summary>
    /// Back-compatible entry point used by <c>nb catalog asyncapi</c>: exports the built-in
    /// platform, inferring the format from the output extension (<c>.json</c> ⇒ JSON, else YAML).
    /// </summary>
    public static Task ExportAsync(string outputPath) =>
        ExportAsync(new PlatformConfiguration(), outputPath, FormatFromPath(outputPath));

    /// <summary>Exports the built-in platform in the requested format.</summary>
    public static Task ExportAsync(string outputPath, AsyncApiFormat format) =>
        ExportAsync(new PlatformConfiguration(), outputPath, format);

    /// <summary>Exports an arbitrary platform (external integration repos, samples, tests).</summary>
    public static async Task ExportAsync(IPlatform platform, string outputPath, AsyncApiFormat format)
    {
        if (platform is null) throw new ArgumentNullException(nameof(platform));

        var content = Serialize(platform, format);
        await File.WriteAllTextAsync(outputPath, content);

        var endpointCount = platform.Endpoints.Count();
        var eventCount = platform.EventTypes.Count() + platform.DynamicForwards.Select(f => f.EventTypeId).Distinct().Count();
        Console.WriteLine($"AsyncAPI 3.0 spec exported to: {outputPath}");
        Console.WriteLine($"  {endpointCount} endpoints, {eventCount} event types ({format.ToString().ToUpperInvariant()})");
    }

    /// <summary>Builds the AsyncAPI document for <paramref name="platform"/> and serializes it.</summary>
    public static string Serialize(IPlatform platform, AsyncApiFormat format)
    {
        if (platform is null) throw new ArgumentNullException(nameof(platform));

        var document = BuildDocument(platform);
        return format == AsyncApiFormat.Json
            ? JsonConvert.SerializeObject(document, Formatting.Indented)
            // WithQuotingNecessaryStrings so values that look like numbers/bools/null (e.g. a
            // protocolVersion of "1.0", or an event/field literally named "123" or "true") round-trip
            // as strings instead of being reinterpreted by a YAML parser.
            : new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(document);
    }

    private static AsyncApiFormat FormatFromPath(string path) =>
        path is not null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? AsyncApiFormat.Json
            : AsyncApiFormat.Yaml;

    private static Map BuildDocument(IPlatform platform)
    {
        var endpointsById = platform.Endpoints
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var events = platform.EventTypes
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var dynamicForwards = platform.DynamicForwards
            .OrderBy(f => f.EventTypeId, StringComparer.Ordinal)
            .ThenBy(f => f.TargetEndpoint, StringComparer.Ordinal)
            .ToList();

        // Which event types appear on each endpoint's topic (produced here, or auto-forwarded in).
        var topicMessages = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void OnTopic(string endpointId, string eventId)
        {
            if (!topicMessages.TryGetValue(endpointId, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                topicMessages[endpointId] = set;
            }
            set.Add(eventId);
        }

        foreach (var evt in events)
        {
            foreach (var producer in platform.GetProducers(evt)) OnTopic(producer.Id, evt.Id);
            foreach (var consumer in platform.GetConsumers(evt)) OnTopic(consumer.Id, evt.Id);
        }
        foreach (var fwd in dynamicForwards)
        {
            OnTopic(fwd.SourceEndpoint, fwd.EventTypeId);
            OnTopic(fwd.TargetEndpoint, fwd.EventTypeId);
        }

        var channels = BuildChannels(topicMessages, endpointsById);
        var operations = BuildOperations(platform, events, dynamicForwards, endpointsById);
        var components = BuildComponents(events, dynamicForwards);

        return new Map
        {
            ["asyncapi"] = "3.0.0",
            ["info"] = new Map
            {
                ["title"] = "NimBus Platform",
                ["version"] = "1.0.0",
                ["description"] =
                    "Event contracts and Azure Service Bus topology generated from NimBus. " +
                    "Each endpoint is a Service Bus topic; events are routed by SQL rules on application " +
                    "properties and auto-forwarded between topics. See the x-servicebus* extensions for the " +
                    "physical delivery details behind each logical operation.",
            },
            ["servers"] = BuildServers(),
            ["channels"] = channels,
            ["operations"] = operations,
            ["components"] = components,
        };
    }

    private static Map BuildServers() => new()
    {
        ["production"] = new Map
        {
            ["host"] = "{namespace}.servicebus.windows.net",
            ["protocol"] = "amqp",
            ["protocolVersion"] = "1.0",
            ["description"] = "Azure Service Bus namespace (AMQP 1.0).",
            // No official AsyncAPI Service Bus binding exists; declare the closest standard binding.
            ["bindings"] = new Map { ["amqp1"] = new Map() },
            ["x-nimbus-topology"] = new Map
            {
                ["pattern"] = "topic-per-endpoint",
                ["routing"] = "sql-rules-on-application-properties",
                ["autoForwarding"] = true,
                ["resolverTopic"] = Constants.ResolverId,
                ["auditSubscription"] = Constants.ResolverId,
                ["deferredSubscriptions"] = new List<object> { Constants.DeferredSubscriptionName, Constants.DeferredProcessorId },
                ["routingProperties"] = new[]
                    {
                        UserPropertyName.To, UserPropertyName.From, UserPropertyName.EventTypeId,
                        UserPropertyName.MessageType, UserPropertyName.EventId, UserPropertyName.RetryCount,
                        UserPropertyName.OriginatingFrom, UserPropertyName.OriginalSessionId,
                    }
                    .Select(p => (object)p.ToString())
                    .ToList(),
            },
        },
    };

    private static Map BuildChannels(
        SortedDictionary<string, SortedSet<string>> topicMessages,
        IReadOnlyDictionary<string, IEndpoint> endpointsById)
    {
        var channels = new Map();
        foreach (var (endpointId, eventIds) in topicMessages)
        {
            var name = endpointsById.TryGetValue(endpointId, out var ep) ? ep.Name : endpointId;
            var messages = new Map();
            foreach (var eventId in eventIds)
            {
                messages[eventId] = new Map { ["$ref"] = $"#/components/messages/{eventId}" };
            }

            channels[endpointId] = new Map
            {
                ["address"] = endpointId,
                ["description"] = $"Azure Service Bus topic for {name}.",
                ["messages"] = messages,
                ["bindings"] = new Map { ["amqp1"] = new Map() },
                ["x-servicebus"] = new Map
                {
                    ["resourceType"] = "topic",
                    ["topic"] = endpointId,
                    ["supportsOrdering"] = true,
                    ["duplicateDetectionHistoryTimeWindow"] = "PT10M",
                },
            };
        }

        return channels;
    }

    private static Map BuildOperations(
        IPlatform platform,
        IReadOnlyList<IEventType> events,
        IReadOnlyList<DynamicForward> dynamicForwards,
        IReadOnlyDictionary<string, IEndpoint> endpointsById)
    {
        var operations = new Map();

        foreach (var evt in events)
        {
            var producers = platform.GetProducers(evt).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            var consumers = platform.GetConsumers(evt).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

            foreach (var producer in producers)
            {
                operations[$"{producer.Id}_send_{OpKey(evt.Id)}"] = SendOperation(producer, evt.Id, evt.Name);
            }

            foreach (var consumer in consumers)
            {
                // Auto-forward sources are every OTHER producer's topic (the provisioner never
                // creates a self-forward for an event a single endpoint both produces and consumes).
                var forwards = producers
                    .Where(p => !string.Equals(p.Id, consumer.Id, StringComparison.Ordinal))
                    .Select(p => ForwardSubscription(p.Id, consumer.Id, evt.Id, subscriptionName: consumer.Id))
                    .ToList();

                operations[$"{consumer.Id}_receive_{OpKey(evt.Id)}"] =
                    ReceiveOperation(consumer, evt.Id, evt.Name, forwards);
            }
        }

        foreach (var fwd in dynamicForwards)
        {
            var sourceName = endpointsById.TryGetValue(fwd.SourceEndpoint, out var s) ? s.Name : fwd.SourceEndpoint;
            var targetName = endpointsById.TryGetValue(fwd.TargetEndpoint, out var t) ? t.Name : fwd.TargetEndpoint;

            operations[$"{fwd.SourceEndpoint}_send_{OpKey(fwd.EventTypeId)}"] =
                SendOperation(fwd.SourceEndpoint, sourceName, fwd.EventTypeId);

            var forward = ForwardSubscription(
                fwd.SourceEndpoint, fwd.TargetEndpoint, fwd.EventTypeId,
                subscriptionName: $"AgentDyn-{fwd.TargetEndpoint}");

            operations[$"{fwd.TargetEndpoint}_receive_{OpKey(fwd.EventTypeId)}"] =
                ReceiveOperation(fwd.TargetEndpoint, targetName, fwd.EventTypeId, new List<Map> { forward });
        }

        return operations;
    }

    private static Map SendOperation(IEndpoint producer, string eventId, string eventName) =>
        SendOperation(producer.Id, producer.Name, eventId, eventName);

    private static Map SendOperation(string producerId, string producerName, string eventId, string eventName = null) =>
        new()
        {
            ["action"] = "send",
            ["title"] = $"{producerName} publishes {eventName ?? eventId}",
            ["channel"] = new Map { ["$ref"] = $"#/channels/{producerId}" },
            ["messages"] = new List<object> { new Map { ["$ref"] = $"#/channels/{producerId}/messages/{eventId}" } },
        };

    private static Map ReceiveOperation(IEndpoint consumer, string eventId, string eventName, List<Map> forwards) =>
        ReceiveOperation(consumer.Id, consumer.Name, eventId, forwards, eventName);

    private static Map ReceiveOperation(string consumerId, string consumerName, string eventId, List<Map> forwards, string eventName = null) =>
        new()
        {
            ["action"] = "receive",
            ["title"] = $"{consumerName} consumes {eventName ?? eventId}",
            ["channel"] = new Map { ["$ref"] = $"#/channels/{consumerId}" },
            ["messages"] = new List<object> { new Map { ["$ref"] = $"#/channels/{consumerId}/messages/{eventId}" } },
            ["x-servicebus-delivery"] = new Map
            {
                ["deliverySubscription"] = new Map
                {
                    ["topic"] = consumerId,
                    ["subscription"] = consumerId,
                    ["requiresSession"] = true,
                    ["filter"] = DeliveryFilter(consumerId),
                },
                ["forwardSubscriptions"] = forwards.Cast<object>().ToList(),
            },
        };

    private static Map ForwardSubscription(string sourceTopic, string target, string eventId, string subscriptionName) =>
        new()
        {
            ["topic"] = sourceTopic,
            ["subscription"] = subscriptionName,
            ["forwardTo"] = target,
            ["requiresSession"] = false,
            ["filter"] = ForwardFilter(eventId),
            ["action"] = ForwardAction(sourceTopic, target),
        };

    private static Map BuildComponents(IReadOnlyList<IEventType> events, IReadOnlyList<DynamicForward> dynamicForwards)
    {
        var messages = new Map();
        var schemas = new Map { ["NimBusMessageHeaders"] = BuildHeadersSchema() };
        var building = new HashSet<Type>();

        foreach (var evt in events)
        {
            var clrType = evt.GetEventClassType();
            if (clrType != null)
            {
                EnsureObjectSchema(clrType, schemas, building);
            }

            messages[evt.Id] = BuildMessage(evt, clrType);
        }

        foreach (var eventTypeId in dynamicForwards.Select(f => f.EventTypeId).Distinct(StringComparer.Ordinal))
        {
            if (!messages.ContainsKey(eventTypeId))
            {
                messages[eventTypeId] = BuildDynamicMessage(eventTypeId);
            }
        }

        return new Map
        {
            ["messages"] = messages,
            ["schemas"] = schemas,
        };
    }

    private static Map BuildMessage(IEventType evt, Type clrType)
    {
        var annotation = clrType?.GetCustomAttribute<AsyncApiMessageAttribute>();
        var typeDescription = clrType?.GetCustomAttribute<DescriptionAttribute>()?.Description;

        var serviceBus = new Map
        {
            ["contentType"] = "application/json",
            ["requiresSession"] = true,
            ["messageIdConvention"] = "{EventTypeId}-{deterministicHash(payload)}",
            ["correlationIdConvention"] = "new GUID per publish unless supplied by the caller",
            ["maxDeliveryCount"] = 10,
            ["deadLetterOnFilterEvaluationExceptions"] = true,
        };

        var sessionKey = clrType?.GetCustomAttribute<SessionKeyAttribute>()?.PropertyName;
        if (!string.IsNullOrEmpty(sessionKey))
        {
            serviceBus["sessionKeyProperty"] = sessionKey;
        }

        var message = new Map
        {
            ["name"] = evt.Id,
            ["title"] = annotation?.Title ?? evt.Name,
            ["summary"] = annotation?.Summary ?? typeDescription ?? $"{evt.Name} event.",
            ["contentType"] = "application/json",
            ["headers"] = new Map { ["$ref"] = "#/components/schemas/NimBusMessageHeaders" },
            ["payload"] = new Map { ["$ref"] = $"#/components/schemas/{evt.Id}" },
            ["bindings"] = new Map { ["amqp1"] = new Map() },
            ["x-servicebus"] = serviceBus,
        };

        if (!string.IsNullOrEmpty(annotation?.Description))
        {
            message["description"] = annotation.Description;
        }

        if (annotation?.Tags is { Length: > 0 } tags)
        {
            message["tags"] = tags.Select(tag => (object)new Map { ["name"] = tag }).ToList();
        }

        var example = TryBuildExample(evt);
        if (example != null)
        {
            message["examples"] = new List<object>
            {
                new Map { ["name"] = "sample", ["payload"] = example },
            };
        }

        return message;
    }

    private static Map BuildDynamicMessage(string eventTypeId) => new()
    {
        ["name"] = eventTypeId,
        ["title"] = eventTypeId,
        ["summary"] = "Dynamically-typed event (spec 022) with no compiled .NET contract; payload schema is not known at build time.",
        ["contentType"] = "application/json",
        ["headers"] = new Map { ["$ref"] = "#/components/schemas/NimBusMessageHeaders" },
        ["bindings"] = new Map { ["amqp1"] = new Map() },
        ["x-nimbus-dynamic"] = true,
        ["x-servicebus"] = new Map
        {
            ["contentType"] = "application/json",
            ["requiresSession"] = true,
            ["maxDeliveryCount"] = 10,
            ["deadLetterOnFilterEvaluationExceptions"] = true,
        },
    };

    private static Map BuildHeadersSchema() => new()
    {
        ["type"] = "object",
        ["description"] =
            "Service Bus application properties (user.*) that NimBus stamps and routes on, plus native " +
            "SessionId/MessageId/CorrelationId. Set on publish and rewritten by forwarding rules.",
        ["properties"] = new Map
        {
            ["To"] = HeaderProp("Routing target: the event type id on an original publish, rewritten to the destination endpoint id by the forwarding rule."),
            ["From"] = HeaderProp("Source endpoint id. Null on an original publish; set by the forwarding rule (the 'From IS NULL' guard prevents forward loops)."),
            ["EventTypeId"] = HeaderProp("Unqualified CLR event class name; the routing key for subscription rules."),
            ["MessageType"] = HeaderProp("Message kind, e.g. EventRequest."),
            ["EventId"] = HeaderProp("Assigned by the forwarding rule (newid())."),
            ["RetryCount"] = new Map { ["type"] = "integer", ["description"] = "Delivery retry counter." },
            ["OriginatingFrom"] = HeaderProp("Publishing endpoint name captured at publish time."),
            ["OriginalSessionId"] = HeaderProp("Session id preserved for deferred/parked messages."),
        },
    };

    private static Map HeaderProp(string description) => new() { ["type"] = "string", ["description"] = description };

    // ---- JSON Schema generation from CLR event contracts ----

    private static void EnsureObjectSchema(Type type, Map schemas, HashSet<Type> building)
    {
        var name = type.Name;
        if (schemas.ContainsKey(name) || building.Contains(type)) return;

        building.Add(type);
        schemas[name] = BuildObjectSchema(type, schemas, building);
    }

    private static Map BuildObjectSchema(Type type, Map schemas, HashSet<Type> building)
    {
        var properties = new Map();
        var required = new List<object>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(p => p.MetadataToken))
        {
            var node = MapType(property.PropertyType, schemas, building);

            var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (description != null) node["description"] = description;

            var range = property.GetCustomAttribute<RangeAttribute>();
            if (range != null)
            {
                node["minimum"] = range.Minimum;
                node["maximum"] = range.Maximum;
            }

            var propertyName = ToCamelCase(property.Name);
            properties[propertyName] = node;
            if (IsRequired(property)) required.Add(propertyName);
        }

        var schema = new Map { ["type"] = "object" };
        var typeDescription = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (typeDescription != null) schema["description"] = typeDescription;
        if (required.Count > 0) schema["required"] = required;
        schema["properties"] = properties;
        return schema;
    }

    private static Map MapType(Type type, Map schemas, HashSet<Type> building)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string) || t == typeof(char)) return new Map { ["type"] = "string" };
        if (t == typeof(Guid)) return new Map { ["type"] = "string", ["format"] = "uuid" };
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return new Map { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(TimeSpan) || t == typeof(Uri)) return new Map { ["type"] = "string" };
        if (t == typeof(bool)) return new Map { ["type"] = "boolean" };
        if (t.IsEnum) return new Map { ["type"] = "string", ["enum"] = Enum.GetNames(t).Cast<object>().ToList() };
        if (IsInteger(t)) return new Map { ["type"] = "integer", ["format"] = (t == typeof(long) || t == typeof(ulong)) ? "int64" : "int32" };
        if (IsNumber(t)) return new Map { ["type"] = "number" };

        var element = GetEnumerableElementType(t);
        if (element != null)
        {
            return new Map { ["type"] = "array", ["items"] = MapType(element, schemas, building) };
        }

        if (t.IsClass || (t.IsValueType && !t.IsPrimitive))
        {
            EnsureObjectSchema(t, schemas, building);
            return new Map { ["$ref"] = $"#/components/schemas/{t.Name}" };
        }

        return new Map { ["type"] = "string" };
    }

    private static bool IsInteger(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

    private static bool IsNumber(Type t) =>
        t == typeof(decimal) || t == typeof(double) || t == typeof(float);

    private static Type GetEnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();

        var enumerable = type.GetInterfaces()
            .Concat(type.IsInterface ? new[] { type } : Array.Empty<Type>())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static bool IsRequired(PropertyInfo property)
    {
        if (property.GetCustomAttribute<RequiredAttribute>() != null) return true;

        // Non-nullable (value types, or NRT-annotated reference types) ⇒ required. Reference types
        // in nullable-oblivious assemblies report Unknown and are treated as optional.
        var nullability = new NullabilityInfoContext().Create(property);
        return nullability.ReadState == NullabilityState.NotNull;
    }

    private static Map TryBuildExample(IEventType evt)
    {
        try
        {
            if (evt.GetEventExample() is not { } example) return null;
            return ToPlainValue(example) as Map;
        }
        catch (Exception)
        {
            // An example is a nice-to-have; never let it break the export.
            return null;
        }
    }

    private static object ToPlainValue(object value)
    {
        if (value is null) return null;

        var type = value.GetType();
        if (value is string s) return s;
        if (value is bool || value is decimal || value is double || value is float ||
            value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
        {
            return value;
        }
        if (value is Guid guid) return guid.ToString();
        if (value is DateTime dt) return dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto) return dto.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        if (type.IsEnum) return value.ToString();

        if (value is System.Collections.IEnumerable enumerable)
        {
            var items = new List<object>();
            foreach (var item in enumerable) items.Add(ToPlainValue(item));
            return items;
        }

        var map = new Map();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            map[ToCamelCase(property.Name)] = ToPlainValue(property.GetValue(value));
        }
        return map;
    }

    private static string OpKey(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

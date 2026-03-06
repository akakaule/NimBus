using Newtonsoft.Json;
using System;

namespace NimBus.MessageStore;

public class MessageDocument
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("eventId")]
    public string EventId { get; set; }

    [JsonProperty("endpointId")]
    public string EndpointId { get; set; }

    [JsonProperty("message")]
    public MessageEntity Message { get; set; }

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeToLive { get; set; }
}

public class AuditDocument
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("eventId")]
    public string EventId { get; set; }

    [JsonProperty("audit")]
    public MessageAuditEntity Audit { get; set; }

    [JsonProperty("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeToLive { get; set; }
}

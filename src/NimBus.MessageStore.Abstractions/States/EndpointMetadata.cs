using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class EndpointMetadata
{
    [JsonProperty(PropertyName = "id")] public string EndpointId { get; set; }
    public string EndpointOwner { get; set; }
    public string EndpointOwnerTeam { get; set; }
    public string EndpointOwnerEmail { get; set; }
    public bool? IsHeartbeatEnabled { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public HeartbeatStatus EndpointHeartbeatStatus { get; set; } = HeartbeatStatus.Unknown;

    public List<TechnicalContact> TechnicalContacts { get; set; }
    public List<Heartbeat> Heartbeats { get; set; }
    public bool? SubscriptionStatus { get; set; } = null;
}
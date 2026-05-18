using Newtonsoft.Json;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class EndpointMetadata
{
    [JsonProperty(PropertyName = "id")] public string EndpointId { get; set; }
    public string EndpointOwner { get; set; }
    public string EndpointOwnerTeam { get; set; }
    public string EndpointOwnerEmail { get; set; }

    public List<TechnicalContact> TechnicalContacts { get; set; }
    public bool? SubscriptionStatus { get; set; } = null;
}
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

public class Heartbeat
{
    public string MessageId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime ReceivedTime { get; set; }
    public DateTime EndTime { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public HeartbeatStatus EndpointHeartbeatStatus { get; set; } = HeartbeatStatus.Unknown;
}
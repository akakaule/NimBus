using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class EndpointMetricsResult
{
    public List<EndpointCount> Published { get; set; } = new();
    public List<EndpointEventTypeCount> Handled { get; set; } = new();
    public List<EndpointCount> Failed { get; set; } = new();
}

public class EndpointCount
{
    public string EndpointId { get; set; }
    public int Count { get; set; }
}

public class EndpointEventTypeCount
{
    public string EndpointId { get; set; }
    public string EventTypeId { get; set; }
    public int Count { get; set; }
}

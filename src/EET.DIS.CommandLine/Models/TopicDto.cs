namespace EET.DIS.CommandLine.Models;

public class TopicDto
{
    public string Name { get; set; }
    public List<SubscriptionDto> Subscriptions { get; set; }
}
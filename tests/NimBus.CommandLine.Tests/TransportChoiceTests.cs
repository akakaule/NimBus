using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class TransportChoiceTests
{
    [Theory]
    [InlineData(null, "ServiceBus")]
    [InlineData("", "ServiceBus")]
    [InlineData("   ", "ServiceBus")]
    [InlineData("servicebus", "ServiceBus")]
    [InlineData("ServiceBus", "ServiceBus")]
    [InlineData("SERVICEBUS", "ServiceBus")]
    [InlineData("sb", "ServiceBus")]
    [InlineData("rabbitmq", "RabbitMq")]
    [InlineData("RabbitMQ", "RabbitMq")]
    [InlineData("rabbit", "RabbitMq")]
    [InlineData("rmq", "RabbitMq")]
    public void ParseTransport_AcceptsValidAliases(string? input, string expectedName)
    {
        Assert.Equal(expectedName, Program.ParseTransport(input).ToString());
    }

    [Theory]
    [InlineData("kafka")]
    [InlineData("nats")]
    [InlineData("sqs")]
    [InlineData("ServiceBusFake")]
    public void ParseTransport_ThrowsOnUnknown(string input)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Program.ParseTransport(input));
        Assert.Contains("Unknown --transport", ex.Message);
    }
}

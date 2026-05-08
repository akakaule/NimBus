namespace NimBus.Core.Diagnostics;

/// <summary>
/// Values for the <c>messaging.system</c> attribute. Aligned with the OTel Azure-messaging
/// semconv (which uses <c>servicebus</c>, not <c>azureservicebus</c>) plus a NimBus-specific
/// <c>nimbus.inmemory</c> for the in-memory transport used in tests. Public so transport
/// providers can pass the right value to <see cref="NimBusInstrumentation"/>.
/// </summary>
public static class MessagingSystem
{
    public const string ServiceBus = "servicebus";
    public const string RabbitMq = "rabbitmq";
    public const string InMemory = "nimbus.inmemory";
}

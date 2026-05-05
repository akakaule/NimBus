using System.Globalization;

namespace NimBus.Transport.RabbitMQ.Topology;

/// <summary>
/// Naming conventions for RabbitMQ topology entities. Centralised so the sender,
/// receiver, topology provisioner, and operator tooling all agree on the exact
/// exchange and queue names without scattering format strings across the code.
/// </summary>
internal static class RabbitMqTopologyConventions
{
    /// <summary>
    /// The consistent-hash exchange messages are published to for routing into
    /// per-key partition queues. Type: <c>x-consistent-hash</c>.
    /// </summary>
    public static string EndpointExchange(string endpointName) => endpointName;

    /// <summary>
    /// Per-endpoint dead-letter exchange. Messages exceeding
    /// <c>MaxDeliveryCount</c> are routed here, then projected into
    /// <c>MessageStore.UnresolvedEvents</c>.
    /// </summary>
    public static string DeadLetterExchange(string endpointName) => $"{endpointName}.dlx";

    /// <summary>
    /// Per-endpoint dead-letter queue bound to <see cref="DeadLetterExchange"/>.
    /// </summary>
    public static string DeadLetterQueue(string endpointName) => $"{endpointName}.dlq";

    /// <summary>
    /// Per-endpoint scheduled-enqueue exchange. Type: <c>x-delayed-message</c>
    /// with <c>x-delayed-type = direct</c>. Used by
    /// <c>ISender.ScheduleMessage</c>.
    /// </summary>
    public static string DelayedExchange(string endpointName) => $"{endpointName}.delayed";

    /// <summary>
    /// Per-endpoint, per-partition queue. Bound to the consistent-hash
    /// exchange with a routing-key weight of <c>"1"</c>.
    /// </summary>
    public static string PartitionQueue(string endpointName, int partitionIndex) =>
        $"{endpointName}.partition.{partitionIndex.ToString(CultureInfo.InvariantCulture)}";
}

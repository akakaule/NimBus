using System.Diagnostics;

namespace NimBus.Core.Diagnostics;

/// <summary>
/// Holds the framework-level <see cref="ActivitySource"/> instances used by every
/// NimBus instrumentation site. Public so transport providers and the
/// <c>NimBus.OpenTelemetry</c> package can emit spans on the canonical sources;
/// external consumers should normally subscribe via the names exposed on
/// <see cref="NimBusInstrumentation"/> rather than referencing these instances.
/// </summary>
public static class NimBusActivitySources
{
    public static readonly ActivitySource Publisher = new(NimBusInstrumentation.PublisherActivitySourceName);

    public static readonly ActivitySource Consumer = new(NimBusInstrumentation.ConsumerActivitySourceName);

    public static readonly ActivitySource Outbox = new(NimBusInstrumentation.OutboxActivitySourceName);

    public static readonly ActivitySource DeferredProcessor = new(NimBusInstrumentation.DeferredProcessorActivitySourceName);

    public static readonly ActivitySource Resolver = new(NimBusInstrumentation.ResolverActivitySourceName);

    public static readonly ActivitySource Store = new(NimBusInstrumentation.StoreActivitySourceName);
}

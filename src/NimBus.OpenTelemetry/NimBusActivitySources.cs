using System.Diagnostics;

namespace NimBus.OpenTelemetry;

/// <summary>
/// Holds the framework-level <see cref="ActivitySource"/> instances used by every
/// NimBus instrumentation site. Internal — call sites use these directly; external
/// consumers should subscribe via the names exposed on <see cref="NimBusInstrumentation"/>.
/// </summary>
internal static class NimBusActivitySources
{
    public static readonly ActivitySource Publisher = new(NimBusInstrumentation.PublisherActivitySourceName);

    public static readonly ActivitySource Consumer = new(NimBusInstrumentation.ConsumerActivitySourceName);

    public static readonly ActivitySource Outbox = new(NimBusInstrumentation.OutboxActivitySourceName);

    public static readonly ActivitySource DeferredProcessor = new(NimBusInstrumentation.DeferredProcessorActivitySourceName);

    public static readonly ActivitySource Resolver = new(NimBusInstrumentation.ResolverActivitySourceName);

    public static readonly ActivitySource Store = new(NimBusInstrumentation.StoreActivitySourceName);
}

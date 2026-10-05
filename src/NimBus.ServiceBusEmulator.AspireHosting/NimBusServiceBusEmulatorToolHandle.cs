using Aspire.Hosting.ApplicationModel;

namespace NimBus.ServiceBusEmulator.AspireHosting;

/// <summary>
/// Groups the emulator, run from its NuGet tool package, with the connection-string resource exposed
/// to consumers.
/// </summary>
/// <param name="Emulator">The emulator executable.</param>
/// <param name="ConnectionString">The connection string to reference from the topology, Resolver, WebApp and adapters.</param>
public readonly record struct NimBusServiceBusEmulatorToolHandle(
    IResourceBuilder<ExecutableResource> Emulator,
    IResourceBuilder<IResourceWithConnectionString> ConnectionString);

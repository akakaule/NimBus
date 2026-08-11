using Aspire.Hosting.ApplicationModel;

namespace NimBus.ServiceBusEmulator.AspireHosting;

/// <summary>
/// Groups the runnable emulator project with the connection-string resource exposed to consumers.
/// </summary>
public readonly record struct NimBusServiceBusEmulatorHandle(
    IResourceBuilder<ProjectResource> Project,
    IResourceBuilder<IResourceWithConnectionString> ConnectionString);

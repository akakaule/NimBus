using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Aspire extensions for the NimBus Service Bus emulator.
/// </summary>
public static class NimBusServiceBusEmulatorBuilderExtensions
{
    /// <summary>
    /// Adds the emulator project and a connection-string resource named <paramref name="name"/>.
    /// </summary>
    public static NimBus.ServiceBusEmulator.AspireHosting.NimBusServiceBusEmulatorHandle
        AddNimBusServiceBusEmulator<TProject>(
            this IDistributedApplicationBuilder builder,
            string name,
            int? port = null)
        where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Configure(builder, name, builder.AddProject<TProject>($"{name}-emulator"), port);
    }

    /// <summary>
    /// Adds an emulator project by project-file path for AppHosts that do not use generated project metadata.
    /// </summary>
    public static NimBus.ServiceBusEmulator.AspireHosting.NimBusServiceBusEmulatorHandle
        AddNimBusServiceBusEmulator(
            this IDistributedApplicationBuilder builder,
            string name,
            string projectPath,
            int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        return Configure(builder, name, builder.AddProject($"{name}-emulator", projectPath), port);
    }

    private static NimBus.ServiceBusEmulator.AspireHosting.NimBusServiceBusEmulatorHandle Configure(
        IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ProjectResource> projectBuilder,
        int? port)
    {
        // The endpoint must stay proxied: DCP requires an explicit port for
        // unproxied endpoints, and a hard-coded default would collide when
        // several AppHosts run emulators side by side. The DCP proxy carries
        // both planes fine — the emulator multiplexes AMQP and HTTP admin by
        // first-byte sniffing on the single target port it gets via env.
        var project = projectBuilder
            .WithEndpoint(
                targetPort: null,
                port: port,
                scheme: "tcp",
                name: "tcp",
                env: "NIMBUS_SBEMULATOR_PORT",
                isExternal: false,
                isProxied: true)
            .WithEnvironment("NIMBUS_SBEMULATOR_RESOURCE_NAME", name);

        var endpoint = project.GetEndpoint("tcp");
        var connectionString = builder.AddConnectionString(
                name,
                ReferenceExpression.Create(
                    $"Endpoint=sb://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=nimbus-local;UseDevelopmentEmulator=true"))
            .WaitFor(project);

        var healthCheckName = $"{name}-emulator-readiness";
        builder.Services.AddHttpClient();
        builder.Services.AddHealthChecks().AddCheck(
            healthCheckName,
            new NimBus.ServiceBusEmulator.AspireHosting.EmulatorEndpointHealthCheck(() => endpoint));
        project.WithHealthCheck(healthCheckName);

        return new(project, connectionString);
    }
}

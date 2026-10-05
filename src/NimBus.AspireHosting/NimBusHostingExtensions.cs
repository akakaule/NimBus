using Aspire.Hosting.ApplicationModel;
using NimBus.ServiceBusEmulator.AspireHosting;

namespace Aspire.Hosting;

/// <summary>
/// Aspire extensions that run the NimBus Resolver, WebApp and topology provisioning from their NuGet
/// tool packages, so an adapter repository's AppHost can run a complete local stack without a NimBus
/// source checkout.
/// </summary>
/// <remarks>
/// Each tool runs with <c>dotnet tool exec</c> in the AppHost directory, so packages restore through
/// that directory's NuGet configuration. The tool version defaults to the <c>NIMBUS_TOOL_VERSION</c>
/// configuration value, then to this package's own version. Wire the Service Bus connection with
/// <see cref="WithNimBusServiceBus{T}"/> and the message store with
/// <see cref="WithNimBusSqlServerStore{T}"/> or <see cref="WithNimBusCosmosStore{T}"/>.
/// </remarks>
public static class NimBusHostingExtensions
{
    /// <summary>The NuGet package that ships the Resolver as a dotnet tool.</summary>
    public const string ResolverToolPackageId = "Akaule.NimBus.Resolver.Host";

    /// <summary>The NuGet package that ships the WebApp as a dotnet tool.</summary>
    public const string WebAppToolPackageId = "Akaule.NimBus.WebApp";

    /// <summary>The NuGet package that ships the <c>nb</c> command line.</summary>
    public const string CommandLineToolPackageId = "Akaule.NimBus.CommandLine";

    /// <summary>
    /// Adds a resource that provisions the Service Bus topology for a platform catalog with
    /// <c>nb topology apply</c>, then exits. Make the Resolver, WebApp and adapters
    /// <c>WaitForCompletion</c> on it.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="serviceBus">The Service Bus (or emulator) connection string to provision.</param>
    /// <param name="platformAssemblyPath">
    /// The absolute path of the assembly that contains the platform catalog, typically
    /// <c>typeof(MyPlatform).Assembly.Location</c> from a contracts project the AppHost references.
    /// </param>
    /// <param name="platformType">The full name of the catalog type, or <c>null</c> when the assembly has only one.</param>
    /// <param name="version">The tool version; see the class remarks for the default.</param>
    /// <returns>The provisioning resource.</returns>
    public static IResourceBuilder<ExecutableResource> AddNimBusTopology(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<IResourceWithConnectionString> serviceBus,
        string platformAssemblyPath,
        string? platformType = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(serviceBus);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAssemblyPath);

        string[] args = string.IsNullOrWhiteSpace(platformType)
            ? ["topology", "apply", "--assembly", platformAssemblyPath]
            : ["topology", "apply", "--assembly", platformAssemblyPath, "--platform", platformType];

        // The connection string goes through the environment, not the command line, so it never
        // shows up in the dashboard's argument list.
        return NimBusTools.AddTool(builder, name, CommandLineToolPackageId, version, args)
            .WithEnvironment("AzureServiceBus_ConnectionString", serviceBus.Resource.ConnectionStringExpression)
            .WaitFor(serviceBus);
    }

    /// <summary>
    /// Adds the NimBus Resolver, run as a worker from its NuGet tool package. Give it the Service
    /// Bus with <see cref="WithNimBusServiceBus{T}"/> and a message store.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The tool version; see the class remarks for the default.</param>
    /// <returns>The Resolver resource.</returns>
    public static IResourceBuilder<ExecutableResource> AddNimBusResolver(
        this IDistributedApplicationBuilder builder,
        string name = "resolver",
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return NimBusTools.AddTool(builder, name, ResolverToolPackageId, version)
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ResolverId", "Resolver");
    }

    /// <summary>
    /// Adds the NimBus WebApp, run from its NuGet tool package in Development with the local sign-in
    /// bypass, showing the given platform catalog. Give it the Service Bus with
    /// <see cref="WithNimBusServiceBus{T}"/> and the same message store as the Resolver.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="platformAssemblyPath">
    /// The absolute path of the assembly that contains the platform catalog, typically
    /// <c>typeof(MyPlatform).Assembly.Location</c>.
    /// </param>
    /// <param name="platformType">The full name of the catalog type.</param>
    /// <param name="httpsPort">A fixed host port for the UI, or <c>null</c> for a dynamic one.</param>
    /// <param name="httpPort">A fixed host port for the plain-HTTP endpoint, or <c>null</c> for a dynamic one.</param>
    /// <param name="version">The tool version; see the class remarks for the default.</param>
    /// <returns>
    /// The WebApp resource, with external <c>https</c> (the UI; the SPA calls its API over HTTPS with
    /// the ASP.NET Core development certificate) and <c>http</c> endpoints.
    /// </returns>
    public static IResourceBuilder<ExecutableResource> AddNimBusWebApp(
        this IDistributedApplicationBuilder builder,
        string name,
        string platformAssemblyPath,
        string platformType,
        int? httpsPort = null,
        int? httpPort = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformType);

        return NimBusTools.AddTool(builder, name, WebAppToolPackageId, version)
            .WithHttpsEndpoint(port: httpsPort, name: "https", env: "ASPNETCORE_HTTPS_PORTS")
            .WithHttpEndpoint(port: httpPort, name: "http", env: "ASPNETCORE_HTTP_PORTS")
            .WithExternalHttpEndpoints()
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            // Local sign-in bypass; the WebApp refuses it outside Development.
            .WithEnvironment("EnableLocalDevAuthentication", "true")
            .WithEnvironment("NimBus__Mcp__EnableForLocalDevelopment", "true")
            .WithEnvironment("NimBus__PlatformType", platformType)
            .WithEnvironment("NimBus__PlatformAssembly", platformAssemblyPath);
    }

    /// <summary>
    /// Points a NimBus Resolver or WebApp at a Service Bus (or emulator) connection string, under the
    /// configuration keys both read, and waits for it.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resource">The Resolver or WebApp resource.</param>
    /// <param name="serviceBus">The Service Bus connection string.</param>
    /// <returns>The resource builder, for chaining.</returns>
    public static IResourceBuilder<T> WithNimBusServiceBus<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<IResourceWithConnectionString> serviceBus)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(serviceBus);

        return resource
            .WithEnvironment("ConnectionStrings__servicebus", serviceBus.Resource.ConnectionStringExpression)
            .WithEnvironment("AzureWebJobsServiceBus", serviceBus.Resource.ConnectionStringExpression)
            .WaitFor(serviceBus);
    }

    /// <summary>
    /// Selects the SQL Server message store for a NimBus Resolver or WebApp and waits for the database.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resource">The Resolver or WebApp resource.</param>
    /// <param name="database">The NimBus database.</param>
    /// <returns>The resource builder, for chaining.</returns>
    public static IResourceBuilder<T> WithNimBusSqlServerStore<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<IResourceWithConnectionString> database)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(database);

        // The provider is set explicitly: auto-detection prefers Cosmos when any Cosmos setting is present.
        return resource
            .WithEnvironment("NimBus__StorageProvider", "sqlserver")
            .WithEnvironment("ConnectionStrings__sqlserver", database.Resource.ConnectionStringExpression)
            .WaitFor(database);
    }

    /// <summary>
    /// Selects the Cosmos DB message store for a NimBus Resolver or WebApp and waits for the account.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resource">The Resolver or WebApp resource.</param>
    /// <param name="cosmos">The Cosmos DB connection string.</param>
    /// <returns>The resource builder, for chaining.</returns>
    public static IResourceBuilder<T> WithNimBusCosmosStore<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<IResourceWithConnectionString> cosmos)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(cosmos);

        return resource
            .WithEnvironment("NimBus__StorageProvider", "cosmos")
            .WithEnvironment("ConnectionStrings__cosmos", cosmos.Resource.ConnectionStringExpression)
            .WaitFor(cosmos);
    }

    /// <summary>
    /// Sends the Resolver's endpoint-state notifications to the WebApp, so its live pages update
    /// with storage providers that have no change feed (SQL Server).
    /// </summary>
    /// <param name="resolver">The Resolver resource.</param>
    /// <param name="webApp">The WebApp resource added with <see cref="AddNimBusWebApp"/>.</param>
    /// <returns>The Resolver resource builder, for chaining.</returns>
    public static IResourceBuilder<ExecutableResource> WithNimBusWebAppNotifications(
        this IResourceBuilder<ExecutableResource> resolver,
        IResourceBuilder<ExecutableResource> webApp)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(webApp);

        return resolver.WithEnvironment("NimBus__Flow__WebAppUrl", webApp.GetEndpoint("http"));
    }
}

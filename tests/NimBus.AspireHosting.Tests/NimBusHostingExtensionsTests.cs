#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.ServiceBusEmulator.AspireHosting;

namespace NimBus.AspireHosting.Tests;

/// <summary>
/// The app model an adapter repository's AppHost gets from the package-based hosting methods:
/// which tool each resource runs, at which version, with which configuration.
/// </summary>
[TestClass]
public sealed class NimBusHostingExtensionsTests
{
    private const string PlatformAssembly = @"C:\repo\Contracts\bin\Debug\net10.0\Acme.Contracts.dll";
    private const string PlatformType = "Acme.Contracts.AcmePlatform";

    [TestMethod]
    public async Task Emulator_RunsTheEmulatorTool_AndExposesTheConnectionString()
    {
        var builder = CreateBuilder("4.3.0");

        var emulator = builder.AddNimBusServiceBusEmulator("servicebus");

        Assert.AreEqual("servicebus-emulator", emulator.Emulator.Resource.Name);
        Assert.AreEqual("dotnet", emulator.Emulator.Resource.Command);
        CollectionAssert.AreEqual(
            new[] { "tool", "exec", "Akaule.NimBus.ServiceBusEmulator@4.3.0", "--" },
            await ArgsOf(emulator.Emulator.Resource));
        Assert.AreEqual("servicebus", emulator.ConnectionString.Resource.Name);
        StringAssert.Contains(
            emulator.ConnectionString.Resource.ConnectionStringExpression.ValueExpression,
            "UseDevelopmentEmulator=true",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Topology_RunsNbTopologyApply_WithTheConnectionStringInTheEnvironment()
    {
        var builder = CreateBuilder("4.3.0");
        var serviceBus = builder.AddConnectionString("servicebus");

        var topology = builder.AddNimBusTopology("topology", serviceBus, PlatformAssembly, PlatformType);

        CollectionAssert.AreEqual(
            new[] { "tool", "exec", "Akaule.NimBus.CommandLine@4.3.0", "--", "topology", "apply", "--assembly", PlatformAssembly, "--platform", PlatformType },
            await ArgsOf(topology.Resource));
        var environment = await EnvironmentOf(topology.Resource);
        Assert.AreEqual("{servicebus.connectionString}", environment["AzureServiceBus_ConnectionString"]);
        Assert.IsTrue(WaitsFor(topology.Resource, "servicebus"));
    }

    [TestMethod]
    public async Task Topology_OmitsThePlatformOption_WhenNoTypeIsGiven()
    {
        var builder = CreateBuilder("4.3.0");
        var serviceBus = builder.AddConnectionString("servicebus");

        var topology = builder.AddNimBusTopology("topology", serviceBus, PlatformAssembly);

        CollectionAssert.DoesNotContain(await ArgsOf(topology.Resource), "--platform");
    }

    [TestMethod]
    public async Task Resolver_RunsTheResolverTool_WithTheServiceBusAndSqlStore()
    {
        var builder = CreateBuilder("4.3.0");
        var serviceBus = builder.AddConnectionString("bus");
        var database = builder.AddConnectionString("nimbusdb");

        var resolver = builder.AddNimBusResolver()
            .WithNimBusServiceBus(serviceBus)
            .WithNimBusSqlServerStore(database);

        Assert.AreEqual("resolver", resolver.Resource.Name);
        CollectionAssert.AreEqual(
            new[] { "tool", "exec", "Akaule.NimBus.Resolver.Host@4.3.0", "--" },
            await ArgsOf(resolver.Resource));
        var environment = await EnvironmentOf(resolver.Resource);
        // The resource is named "bus", but the Resolver reads the "servicebus" connection string.
        Assert.AreEqual("{bus.connectionString}", environment["ConnectionStrings__servicebus"]);
        Assert.AreEqual("{bus.connectionString}", environment["AzureWebJobsServiceBus"]);
        Assert.AreEqual("sqlserver", environment["NimBus__StorageProvider"]);
        Assert.AreEqual("{nimbusdb.connectionString}", environment["ConnectionStrings__sqlserver"]);
        Assert.AreEqual("Resolver", environment["ResolverId"]);
        Assert.IsTrue(WaitsFor(resolver.Resource, "bus"));
        Assert.IsTrue(WaitsFor(resolver.Resource, "nimbusdb"));
    }

    [TestMethod]
    public async Task WebApp_RunsInDevelopment_WithTheLocalSignInBypass_AndThePlatformCatalog()
    {
        var builder = CreateBuilder("4.3.0");
        var cosmos = builder.AddConnectionString("cosmos");

        var webApp = builder.AddNimBusWebApp("nimbus-ops", PlatformAssembly, PlatformType, httpsPort: 18643, httpPort: 18280)
            .WithNimBusCosmosStore(cosmos);

        CollectionAssert.AreEqual(
            new[] { "tool", "exec", "Akaule.NimBus.WebApp@4.3.0", "--" },
            await ArgsOf(webApp.Resource));
        var environment = await EnvironmentOf(webApp.Resource);
        Assert.AreEqual("Development", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.AreEqual("true", environment["EnableLocalDevAuthentication"]);
        Assert.AreEqual(PlatformType, environment["NimBus__PlatformType"]);
        Assert.AreEqual(PlatformAssembly, environment["NimBus__PlatformAssembly"]);
        Assert.AreEqual("cosmos", environment["NimBus__StorageProvider"]);
        Assert.AreEqual("{cosmos.connectionString}", environment["ConnectionStrings__cosmos"]);

        // The SPA calls its API over HTTPS, so the UI endpoint is https.
        var https = webApp.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "https");
        Assert.AreEqual("https", https.UriScheme);
        Assert.AreEqual(18643, https.Port);
        Assert.IsTrue(https.IsExternal);
        var http = webApp.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "http");
        Assert.AreEqual(18280, http.Port);
    }

    [TestMethod]
    public async Task ResolverNotifications_PointAtTheWebApp()
    {
        var builder = CreateBuilder("4.3.0");
        var webApp = builder.AddNimBusWebApp("nimbus-ops", PlatformAssembly, PlatformType);

        var resolver = builder.AddNimBusResolver().WithNimBusWebAppNotifications(webApp);

        var environment = await EnvironmentOf(resolver.Resource);
        Assert.AreEqual("{nimbus-ops.bindings.http.url}", environment["NimBus__Flow__WebAppUrl"]);
    }

    [TestMethod]
    public async Task AnExplicitVersion_WinsOverTheConfiguredOne()
    {
        var builder = CreateBuilder("4.3.0");

        var resolver = builder.AddNimBusResolver(version: "4.4.0-preview.1");

        CollectionAssert.Contains(await ArgsOf(resolver.Resource), "Akaule.NimBus.Resolver.Host@4.4.0-preview.1");
    }

    [TestMethod]
    [DataRow(null, null, "4.3.0+abc123", "4.3.0", DisplayName = "The hosting package version, without build metadata")]
    [DataRow(null, "4.3.0-local", "4.3.0", "4.3.0-local", DisplayName = "NIMBUS_TOOL_VERSION overrides the package version")]
    [DataRow("4.2.0", "4.3.0-local", "4.3.0", "4.2.0", DisplayName = "An explicit version wins")]
    [DataRow(null, null, "0.0.0+abc123", null, DisplayName = "A development build lets NuGet pick the latest")]
    [DataRow(null, " ", null, null, DisplayName = "Nothing known")]
    public void ResolveVersion_PrefersExplicitThenConfiguredThenThePackagesOwn(
        string? explicitVersion, string? configured, string? hosting, string? expected)
    {
        Assert.AreEqual(expected, NimBusTools.ResolveVersion(explicitVersion, configured, hosting));
    }

    private static IDistributedApplicationBuilder CreateBuilder(string toolVersion)
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--publisher", "manifest", "--output-path", "unused"]);
        builder.Configuration[NimBusTools.VersionOverrideKey] = toolVersion;
        return builder;
    }

    // Run the resource's argument and environment callbacks, rendering references as manifest
    // expressions ({servicebus.connectionString}) so nothing has to be allocated or started.
    private static async Task<string[]> ArgsOf(IResource resource)
    {
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args);
        foreach (var annotation in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return args.Select(Render).ToArray();
    }

    private static async Task<Dictionary<string, string>> EnvironmentOf(IResource resource)
    {
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            values);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return values.ToDictionary(pair => pair.Key, pair => Render(pair.Value));
    }

    private static string Render(object value) => value switch
    {
        string text => text,
        IManifestExpressionProvider expression => expression.ValueExpression,
        _ => value.ToString() ?? string.Empty,
    };

    private static bool WaitsFor(IResource resource, string dependency) =>
        resource.Annotations.OfType<WaitAnnotation>().Any(w => w.Resource.Name == dependency);
}

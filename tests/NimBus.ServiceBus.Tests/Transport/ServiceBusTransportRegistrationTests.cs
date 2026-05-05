#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.ServiceBus.Transport;
using NimBus.Testing;
using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Tests.Transport;

[TestClass]
public class ServiceBusTransportRegistrationTests
{
    private static ServiceCollection NewServicesWithConfig()
    {
        var services = new ServiceCollection();
        // ServiceBusTransportOptions probes IConfiguration for legacy connection-string
        // keys (AzureWebJobsServiceBus / ConnectionStrings:servicebus). The probe only
        // fires when the user-supplied options are blank, but the dependency on
        // IConfiguration is present unconditionally — supply an empty one for tests.
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddSingleton(configuration);
        return services;
    }

    [TestMethod]
    public void AddServiceBusTransport_RegistersExactlyOneProviderRegistration()
    {
        var services = NewServicesWithConfig();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddServiceBusTransport(o =>
            {
                o.ConnectionString = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v";
            });
        });

        var sp = services.BuildServiceProvider();
        var registrations = sp.GetServices<ITransportProviderRegistration>().ToList();

        Assert.AreEqual(1, registrations.Count);
        Assert.AreEqual("Azure Service Bus", registrations[0].ProviderName);
    }

    [TestMethod]
    public void AddServiceBusTransport_RegistersExpectedCapabilities()
    {
        var services = NewServicesWithConfig();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddServiceBusTransport(o =>
            {
                o.ConnectionString = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v";
            });
        });

        var sp = services.BuildServiceProvider();
        var capabilities = sp.GetRequiredService<ITransportCapabilities>();

        Assert.IsTrue(capabilities.SupportsNativeSessions);
        Assert.IsTrue(capabilities.SupportsScheduledEnqueue);
        Assert.IsTrue(capabilities.SupportsAutoForward);
        Assert.IsNull(capabilities.MaxOrderingPartitions);
    }

    [TestMethod]
    public void AddServiceBusTransport_ExposesOptionsThroughIOptions()
    {
        var services = NewServicesWithConfig();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddServiceBusTransport(o => o.FullyQualifiedNamespace = "contoso.servicebus.windows.net");
        });

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<ServiceBusTransportOptions>>().Value;

        Assert.AreEqual("contoso.servicebus.windows.net", options.FullyQualifiedNamespace);
        Assert.IsNull(options.ConnectionString);
        Assert.IsNull(options.Credential);
    }

    [TestMethod]
    public void AddServiceBusTransport_CalledTwice_FailsValidation()
    {
        var services = NewServicesWithConfig();

        var ex = Assert.ThrowsException<InvalidOperationException>(() => services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddServiceBusTransport();
            b.AddServiceBusTransport();
        }));

        StringAssert.Contains(ex.Message, "More than one");
    }
}

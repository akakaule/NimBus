#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
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
    [TestMethod]
    public void AddServiceBusTransport_RegistersExactlyOneProviderRegistration()
    {
        var services = new ServiceCollection();
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
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddServiceBusTransport();
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
        var services = new ServiceCollection();
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
        var services = new ServiceCollection();

        var ex = Assert.ThrowsException<InvalidOperationException>(() => services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddServiceBusTransport();
            b.AddServiceBusTransport();
        }));

        StringAssert.Contains(ex.Message, "More than one");
    }
}

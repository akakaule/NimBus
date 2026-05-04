#pragma warning disable CA1707, CA2007
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Testing;
using NimBus.Transport.Abstractions;

namespace NimBus.Core.Tests;

[TestClass]
public class TransportProviderValidationTests
{
    [TestMethod]
    public void AddNimBus_WithoutTransportProvider_ThrowsClearError()
    {
        var services = new ServiceCollection();
        var ex = Assert.ThrowsException<InvalidOperationException>(() => services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
        }));
        StringAssert.Contains(ex.Message, "AddServiceBusTransport");
        StringAssert.Contains(ex.Message, "AddRabbitMqTransport");
    }

    [TestMethod]
    public void AddNimBus_WithMultipleTransportProviders_ThrowsClearError()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransportProviderRegistration>(new FakeTransportRegistration("First"));
        services.AddSingleton<ITransportProviderRegistration>(new FakeTransportRegistration("Second"));

        var ex = Assert.ThrowsException<InvalidOperationException>(() => services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
        }));
        StringAssert.Contains(ex.Message, "More than one");
    }

    [TestMethod]
    public void AddNimBus_WithoutTransport_OptOutSucceeds()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.WithoutTransport();
        });

        Assert.IsNotNull(services.BuildServiceProvider());
    }

    private sealed class FakeTransportRegistration : ITransportProviderRegistration
    {
        public FakeTransportRegistration(string name) => ProviderName = name;
        public string ProviderName { get; }
    }
}

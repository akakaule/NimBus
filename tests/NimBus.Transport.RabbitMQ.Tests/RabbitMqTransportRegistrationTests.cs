#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Testing;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ;
using NimBus.Transport.RabbitMQ.Extensions;

namespace NimBus.Transport.RabbitMQ.Tests;

[TestClass]
public class RabbitMqTransportRegistrationTests
{
    [TestMethod]
    public void AddRabbitMqTransport_RegistersExactlyOneProviderRegistration()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o => o.HostName = "localhost");
        });

        var sp = services.BuildServiceProvider();
        var registrations = sp.GetServices<ITransportProviderRegistration>().ToList();

        Assert.AreEqual(1, registrations.Count);
        Assert.AreEqual("RabbitMQ", registrations[0].ProviderName);
    }

    [TestMethod]
    public void AddRabbitMqTransport_RegistersExpectedCapabilities()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o =>
            {
                o.HostName = "localhost";
                o.PartitionsPerEndpoint = 32;
            });
        });

        var sp = services.BuildServiceProvider();
        var capabilities = sp.GetRequiredService<ITransportCapabilities>();

        Assert.IsFalse(capabilities.SupportsNativeSessions);
        Assert.IsTrue(capabilities.SupportsScheduledEnqueue);
        Assert.IsFalse(capabilities.SupportsAutoForward);
        Assert.AreEqual(32, capabilities.MaxOrderingPartitions);
    }

    [TestMethod]
    public void AddRabbitMqTransport_DefaultsPartitionsTo16()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport();
        });

        var sp = services.BuildServiceProvider();
        var capabilities = sp.GetRequiredService<ITransportCapabilities>();

        Assert.AreEqual(16, capabilities.MaxOrderingPartitions);
    }

    [TestMethod]
    public void AddRabbitMqTransport_ExposesOptionsThroughIOptions()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o =>
            {
                o.HostName = "rabbitmq.internal";
                o.UserName = "nimbus";
                o.Password = "secret";
                o.PartitionsPerEndpoint = 8;
                o.MaxDeliveryCount = 5;
            });
        });

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<RabbitMqTransportOptions>>().Value;

        Assert.AreEqual("rabbitmq.internal", options.HostName);
        Assert.AreEqual("nimbus", options.UserName);
        Assert.AreEqual("secret", options.Password);
        Assert.AreEqual(8, options.PartitionsPerEndpoint);
        Assert.AreEqual(5, options.MaxDeliveryCount);
    }

    [TestMethod]
    public void AddRabbitMqTransport_RegistersTransportManagement()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o => o.HostName = "localhost");
        });

        var sp = services.BuildServiceProvider();
        var management = sp.GetRequiredService<ITransportManagement>();

        Assert.IsNotNull(management);
    }

    [TestMethod]
    public void AddRabbitMqTransport_RegistersSenderFactory()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o => o.HostName = "localhost");
        });

        var sp = services.BuildServiceProvider();
        var senderFactory = sp.GetRequiredService<Func<string, ISender>>();

        var sender = senderFactory("test-endpoint");
        Assert.IsNotNull(sender);
        Assert.IsInstanceOfType(sender, typeof(RabbitMqSender));
    }

    [TestMethod]
    public void AddRabbitMqTransport_CalledTwice_FailsValidation()
    {
        var services = new ServiceCollection();

        var ex = Assert.ThrowsException<InvalidOperationException>(() => services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o => o.HostName = "localhost");
            b.AddRabbitMqTransport(o => o.HostName = "localhost");
        }));

        StringAssert.Contains(ex.Message, "More than one");
    }

    [TestMethod]
    public void AddRabbitMqTransport_PartitionsZero_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b =>
        {
            b.AddInMemoryMessageStore();
            b.AddRabbitMqTransport(o =>
            {
                o.HostName = "localhost";
                o.PartitionsPerEndpoint = 0;
            });
        });

        var sp = services.BuildServiceProvider();
        Assert.ThrowsException<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<RabbitMqTransportOptions>>().Value);
    }
}

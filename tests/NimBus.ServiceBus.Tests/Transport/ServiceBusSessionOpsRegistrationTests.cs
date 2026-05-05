#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.ServiceBus.Transport;
using NimBus.Testing;
using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Tests.Transport;

[TestClass]
public class ServiceBusSessionOpsRegistrationTests
{
    private static ServiceCollection NewServicesWithConfig()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddSingleton(configuration);
        return services;
    }

    [TestMethod]
    public void AddServiceBusTransport_RegistersITransportSessionOps()
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
        var sessionOps = sp.GetRequiredService<ITransportSessionOps>();

        Assert.IsInstanceOfType(sessionOps, typeof(ServiceBusSessionOps));
    }
}

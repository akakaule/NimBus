#pragma warning disable CA1707, CA2007

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Agents;
using NimBus.Agents.Internal;

namespace NimBus.Agents.Tests;

[TestClass]
public class AgentServiceCollectionExtensionsTests
{
    private sealed record Ping(string Value);

    private sealed class TestHandler : IAgentHandler<Ping>
    {
        public Task<AgentResult> HandleAsync(AgentContext<Ping> context, CancellationToken cancellationToken) =>
            Task.FromResult(AgentResult.Done());
    }

    [TestMethod]
    public void AddNimBusAgent_MissingAgentId_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsException<ArgumentException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o => o.Subscribe("Ping")));
    }

    [TestMethod]
    public void AddNimBusAgent_MissingSubscribe_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsException<ArgumentException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o => o.AgentId = "a"));
    }

    [TestMethod]
    public void AddNimBusAgent_WaitSecondsOutOfRange_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsException<ArgumentException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o =>
            {
                o.AgentId = "a";
                o.Subscribe("Ping");
                o.ReceiveWaitSeconds = 61;
            }));
    }

    [TestMethod]
    public void AddNimBusAgent_Valid_RegistersHandlerGatewayAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNimBusAgent<TestHandler, Ping>(o =>
        {
            o.AgentId = "enrichment-agent";
            o.Subscribe("Ping");
            o.BaseAddress = "http://nimbus-ops.test";
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType(provider.GetRequiredService<IAgentHandler<Ping>>(), typeof(TestHandler));
        Assert.IsNotNull(provider.GetRequiredService<IAgentBusGateway>());
        Assert.IsTrue(
            provider.GetServices<IHostedService>().Any(h => h is AgentLoopWorker<Ping>),
            "The agent loop should be registered as a hosted service.");
    }
}

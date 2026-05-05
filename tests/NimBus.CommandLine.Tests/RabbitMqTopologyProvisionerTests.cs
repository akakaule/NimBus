using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class RabbitMqTopologyProvisionerTests
{
    [Fact]
    public async Task ApplyAsync_DeclaresResolverAndPlatformEndpoints()
    {
        var recordingManagement = new RecordingTransportManagement();
        var options = new RabbitMqTransportOptions { HostName = "localhost" };
        var sut = new RabbitMqTopologyProvisioner(
            options,
            () => new TestPlatform(new TestEndpoint("crm"), new TestEndpoint("erp")),
            _ => recordingManagement);

        await sut.ApplyAsync(CancellationToken.None);

        Assert.Equal(3, recordingManagement.DeclaredEndpoints.Count);
        Assert.Contains(recordingManagement.DeclaredEndpoints, e => e.Name == Constants.ResolverId);
        Assert.Contains(recordingManagement.DeclaredEndpoints, e => e.Name == "crm");
        Assert.Contains(recordingManagement.DeclaredEndpoints, e => e.Name == "erp");
    }

    [Fact]
    public async Task ApplyAsync_DeclaresEndpointsInDeterministicOrder()
    {
        var recordingManagement = new RecordingTransportManagement();
        var options = new RabbitMqTransportOptions { HostName = "localhost" };
        var sut = new RabbitMqTopologyProvisioner(
            options,
            () => new TestPlatform(new TestEndpoint("zeta"), new TestEndpoint("alpha"), new TestEndpoint("mu")),
            _ => recordingManagement);

        await sut.ApplyAsync(CancellationToken.None);

        // Resolver first, then platform endpoints in ordinal sort order so
        // re-running the command never produces churn purely from iteration
        // order changes.
        var declaredNames = recordingManagement.DeclaredEndpoints.Select(e => e.Name).ToArray();
        Assert.Equal(new[] { Constants.ResolverId, "alpha", "mu", "zeta" }, declaredNames);
    }

    [Fact]
    public async Task ApplyAsync_RequiresOrderedDelivery()
    {
        var recordingManagement = new RecordingTransportManagement();
        var options = new RabbitMqTransportOptions { HostName = "localhost" };
        var sut = new RabbitMqTopologyProvisioner(
            options,
            () => new TestPlatform(new TestEndpoint("orders")),
            _ => recordingManagement);

        await sut.ApplyAsync(CancellationToken.None);

        Assert.All(recordingManagement.DeclaredEndpoints, e => Assert.True(e.RequiresOrderedDelivery));
    }

    private sealed class RecordingTransportManagement : ITransportManagement
    {
        public List<EndpointConfig> DeclaredEndpoints { get; } = new();

        public Task DeclareEndpointAsync(EndpointConfig config, CancellationToken cancellationToken)
        {
            DeclaredEndpoints.Add(config);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EndpointInfo>> ListEndpointsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EndpointInfo>>(Array.Empty<EndpointInfo>());

        public Task PurgeEndpointAsync(string endpointName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveEndpointAsync(string endpointName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    private sealed class TestEndpoint : IEndpoint
    {
        public TestEndpoint(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}

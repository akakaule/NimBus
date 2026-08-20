using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Covers <see cref="CosmosContainerProvisioner"/>: `nb topology apply` / `nb setup` provision
/// one Cosmos SQL container per catalog endpoint through the control plane, because Entra
/// data-plane RBAC cannot create containers lazily. Planning (endpoint list → container specs,
/// reserved-id rejection) is pure; the az invocations are asserted through a recording
/// <see cref="IAzureCliRunner"/>.
/// </summary>
public sealed class CosmosContainerProvisionerTests
{
    [Fact]
    public void PlanContainers_MapsEveryEndpointToAContainerWithTheRuntimeDefaults()
    {
        var platform = new TestPlatform(new TestEndpoint("CrmEndpoint"), new TestEndpoint("ErpEndpoint"));

        var specs = CosmosContainerProvisioner.PlanContainers(platform);

        Assert.Equal(2, specs.Count);
        Assert.All(specs, spec =>
        {
            // Must match CosmosContainerDefaults exactly, or control-plane-created and
            // runtime-lazily-created containers would diverge.
            Assert.Equal("/id", spec.PartitionKeyPath);
            Assert.Equal(-1, spec.DefaultTimeToLive);
        });
        Assert.Equal(new[] { "CrmEndpoint", "ErpEndpoint" }, specs.Select(spec => spec.ContainerId));
    }

    [Fact]
    public void PlanContainers_RejectsAnEndpointIdReservedByTheMessageStore()
    {
        // 'messages' is one of the container ids the Cosmos message store owns; an endpoint
        // with that id would share the store's physical container.
        var platform = new TestPlatform(new TestEndpoint("CrmEndpoint"), new TestEndpoint("messages"));

        var exception = Assert.Throws<CommandException>(() => CosmosContainerProvisioner.PlanContainers(platform));

        Assert.Contains("messages", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_ForSqlServer_DoesNotTouchAzure()
    {
        var az = new RecordingAzureCliRunner();
        var sut = new CosmosContainerProvisioner(az, () => new TestPlatform(new TestEndpoint("CrmEndpoint")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), StorageProviderChoice.SqlServer, CancellationToken.None);

        Assert.Empty(az.Invocations);
    }

    [Fact]
    public async Task ApplyAsync_CreatesMissingContainersWithConventionAccountAndRuntimeDefaults()
    {
        var az = new RecordingAzureCliRunner();
        var sut = new CosmosContainerProvisioner(az, () => new TestPlatform(new TestEndpoint("CrmEndpoint")));

        await sut.ApplyAsync(new TopologyOptions("NimBus", "Dev", "rg-test"), StorageProviderChoice.Cosmos, CancellationToken.None);

        var create = Assert.Single(az.Invocations, invocation => invocation.Contains("create"));
        // Account name follows the deployment convention with normalized (lowercased) parts.
        Assert.Equal(
            new[]
            {
                "cosmosdb", "sql", "container", "create",
                "--resource-group", "rg-test",
                "--account-name", "cosmos-nimbus-dev",
                "--database-name", "MessageDatabase",
                "--name", "CrmEndpoint",
                "--partition-key-path", "/id",
                "--ttl", "-1",
            },
            create);
    }

    [Fact]
    public async Task ApplyAsync_LeavesExistingContainersUntouched()
    {
        // Idempotent re-runs: an operator may have customized an existing container's TTL,
        // so the provisioner must not issue any mutating call for it.
        var az = new RecordingAzureCliRunner();
        az.ExistingContainers.Add("CrmEndpoint");
        var sut = new CosmosContainerProvisioner(az, () => new TestPlatform(new TestEndpoint("CrmEndpoint"), new TestEndpoint("ErpEndpoint")));

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), StorageProviderChoice.Cosmos, CancellationToken.None);

        var create = Assert.Single(az.Invocations, invocation => invocation.Contains("create"));
        Assert.Contains("ErpEndpoint", create);
        Assert.DoesNotContain(az.Invocations, invocation => invocation.Contains("create") && invocation.Contains("CrmEndpoint"));
    }

    [Fact]
    public async Task ApplyAsync_WithoutAFactory_ProvisionsTheBuiltInCatalog()
    {
        var az = new RecordingAzureCliRunner();
        var sut = new CosmosContainerProvisioner(az);

        await sut.ApplyAsync(new TopologyOptions("nimbus", "dev", "rg-test"), StorageProviderChoice.Cosmos, CancellationToken.None);

        var builtInEndpointIds = new PlatformConfiguration().Endpoints.Select(endpoint => endpoint.Id).ToList();
        Assert.NotEmpty(builtInEndpointIds);
        foreach (var endpointId in builtInEndpointIds)
        {
            Assert.Contains(az.Invocations, invocation => invocation.Contains("create") && invocation.Contains(endpointId));
        }
    }

    private sealed class RecordingAzureCliRunner : IAzureCliRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = new();

        public HashSet<string> ExistingContainers { get; } = new(StringComparer.Ordinal);

        public Task EnsureLoggedInAsync(CancellationToken cancellationToken)
        {
            Invocations.Add(new[] { "account", "show" });
            return Task.CompletedTask;
        }

        public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken)
        {
            Invocations.Add(new[] { "extension", "add", extensionName });
            return Task.CompletedTask;
        }

        public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Invocations.Add(arguments);
            return Task.CompletedTask;
        }

        public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
        {
            Invocations.Add(arguments);
            return Task.CompletedTask;
        }

        public Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Invocations.Add(arguments);
            var containerId = arguments[arguments.ToList().IndexOf("--name") + 1];
            return Task.FromResult(ExistingContainers.Contains(containerId) ? "true" : "false");
        }

        public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Invocations.Add(arguments);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
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

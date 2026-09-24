#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// 'nb topology apply' addresses the namespace 'nb infra apply' deployed: an explicit
/// override wins, otherwise the override recorded on the resource group, otherwise the
/// derived name.
/// </summary>
public sealed class TopologyNamespaceResolutionTests
{
    [Fact]
    public async Task ResolveNamesAsync_FollowsTheRecordedOverride()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("group") ? "sb-nimbus-dev-premium" : null,
        };

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(azureCli, new TopologyOptions("nimbus", "dev", "rg-nimbus-dev"), CancellationToken.None);

        Assert.Equal("sb-nimbus-dev-premium", names.ServiceBusNamespace);
    }

    [Fact]
    public async Task ResolveNamesAsync_ExplicitOverrideWinsWithoutReadingTags()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("group") ? "sb-recorded" : null,
        };

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(
            azureCli,
            new TopologyOptions("nimbus", "dev", "rg-nimbus-dev", "sb-explicit-name"),
            CancellationToken.None);

        Assert.Equal("sb-explicit-name", names.ServiceBusNamespace);
        Assert.Empty(azureCli.Commands);
    }

    [Fact]
    public async Task ResolveNamesAsync_WithoutARecordKeepsTheDerivedName()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("group") ? string.Empty : null,
        };

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(azureCli, new TopologyOptions("nimbus", "dev", "rg-nimbus-dev"), CancellationToken.None);

        Assert.Equal("sb-nimbus-dev", names.ServiceBusNamespace);
    }
}

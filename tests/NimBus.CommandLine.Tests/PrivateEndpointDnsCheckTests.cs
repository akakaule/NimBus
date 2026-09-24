#pragma warning disable CA1707, CA2007

using System.Net;
using System.Net.Sockets;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The pre-flight DNS check (spec 034 §5.7): before a data-plane step, the machine running
/// nb must resolve each private endpoint's host names to the endpoint's own address.
/// </summary>
[Collection(ExtractedTemplatesCollection.Name)]
public sealed class PrivateEndpointDnsCheckTests
{
    private const string Namespace = "sb-nimbus-dev.servicebus.windows.net";
    private static readonly IPAddress EndpointIp = IPAddress.Parse("10.20.0.4");

    [Fact]
    public void Classify_MatchesTheEndpointAddress()
    {
        Assert.Equal(DnsCheckOutcome.Match, PrivateEndpointDnsCheck.Classify(EndpointIp, new[] { EndpointIp }));
    }

    [Fact]
    public void Classify_NoAnswerMeansTheRecordIsMissing()
    {
        Assert.Equal(DnsCheckOutcome.NoRecord, PrivateEndpointDnsCheck.Classify(EndpointIp, null));
        Assert.Equal(DnsCheckOutcome.NoRecord, PrivateEndpointDnsCheck.Classify(EndpointIp, Array.Empty<IPAddress>()));
    }

    [Theory]
    [InlineData("20.38.116.190")]
    [InlineData("40.113.176.33")]
    public void Classify_PublicAnswerMeansThisMachineIsOutsideThePrivateZone(string address)
    {
        Assert.Equal(DnsCheckOutcome.PublicAddress, PrivateEndpointDnsCheck.Classify(EndpointIp, new[] { IPAddress.Parse(address) }));
    }

    [Theory]
    [InlineData("10.20.0.9")]
    [InlineData("172.16.4.4")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.7")]
    public void Classify_AnotherPrivateAnswerIsAStaleRecord(string address)
    {
        Assert.Equal(DnsCheckOutcome.OtherPrivateAddress, PrivateEndpointDnsCheck.Classify(EndpointIp, new[] { IPAddress.Parse(address) }));
    }

    [Fact]
    public async Task VerifyAsync_PassesAtOnceWhenEveryNameResolvesToItsEndpoint()
    {
        var clock = new FakeClock();
        var check = Check(host => new[] { EndpointIp }, clock);

        var result = await check.VerifyAsync(new[] { Expected() }, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.Zero, clock.Waited);
    }

    /// <summary>External DNS: the customer's policy writes the record a few minutes later.</summary>
    [Fact]
    public async Task VerifyAsync_WaitsForARecordThatArrivesLate()
    {
        var clock = new FakeClock();
        var check = Check(host => clock.Waited < TimeSpan.FromMinutes(2) ? null : new[] { EndpointIp }, clock);

        var result = await check.VerifyAsync(new[] { Expected() }, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.InRange(clock.Waited, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task VerifyAsync_GivesUpAtTheDeadlineAndNamesTheHostAndCause()
    {
        var clock = new FakeClock();
        var check = Check(host => new[] { IPAddress.Parse("20.38.116.190") }, clock);

        var result = await check.VerifyAsync(new[] { Expected() }, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.InRange(clock.Waited, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));
        var failure = Assert.Single(result.Failures);
        Assert.Equal(Namespace, failure.Fqdn);
        Assert.Equal(DnsCheckOutcome.PublicAddress, failure.Outcome);
        Assert.Contains(Namespace, result.Describe(), StringComparison.Ordinal);
        Assert.Contains("does not use the private DNS zone", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_ZeroWaitChecksOnce()
    {
        var clock = new FakeClock();
        var lookups = 0;
        var check = Check(host => { lookups++; return null; }, clock);

        var result = await check.VerifyAsync(new[] { Expected() }, TimeSpan.Zero, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, lookups);
        Assert.Equal(TimeSpan.Zero, clock.Waited);
    }

    [Fact]
    public async Task ReadExpectedAsync_TakesNamesAndAddressFromTheEndpointNic()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments =>
                arguments.Contains("private-endpoint") ? "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/pe-nic"
                : arguments.Contains("nic") ? """[{"ip":"10.20.0.5","fqdns":["webapp-x-dev-management.azurewebsites.net","webapp-x-dev-management.scm.azurewebsites.net"]}]"""
                : null,
        };
        var check = new PrivateEndpointDnsCheck(azureCli);

        var expected = await check.ReadExpectedAsync("rg", new[] { "pe-webapp-x-dev-management-sites" }, CancellationToken.None);

        Assert.Equal(2, expected.Count);
        Assert.All(expected, e => Assert.Equal(IPAddress.Parse("10.20.0.5"), e.Address));
        Assert.Contains(expected, e => e.Fqdn == "webapp-x-dev-management.scm.azurewebsites.net");
    }

    [Fact]
    public void EndpointNames_MatchTheBicepTemplates()
    {
        var context = new CommandContext(null);
        var core = File.ReadAllText(context.CoreBicepPath);
        var webApp = File.ReadAllText(context.WebAppBicepPath);

        Assert.Equal("pe-sb-x-dev-namespace", PrivateEndpointNames.ServiceBus("sb-x-dev"));
        Assert.Equal("pe-func-x-dev-resolver-sites", PrivateEndpointNames.Site("func-x-dev-resolver"));
        Assert.Contains("name: 'pe-${sbNamespace}-namespace'", core, StringComparison.Ordinal);
        Assert.Contains("name: 'pe-${resolverFunctionAppName}-sites'", core, StringComparison.Ordinal);
        Assert.Contains("name: 'pe-${managementWebAppName}-sites'", webApp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunIfPrivateAsync_SkipsDeploymentsWithoutARecordedPrivateState()
    {
        var azureCli = NetworkWithRecordedState(recordedMode: null);

        await PrivateNetworkPreflight.RunIfPrivateAsync(
            azureCli, UnreachableCheck(azureCli), "rg", new[] { "pe-sb-nimbus-dev-namespace" }, TimeSpan.Zero, "testing", CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("private-endpoint", StringComparer.Ordinal));
    }

    /// <summary>In the transition public access still works, so a mismatch only warns.</summary>
    [Fact]
    public async Task RunIfPrivateAsync_OnlyWarnsDuringTheTransition()
    {
        var azureCli = NetworkWithRecordedState("private-transition");

        await PrivateNetworkPreflight.RunIfPrivateAsync(
            azureCli, UnreachableCheck(azureCli), "rg", new[] { "pe-sb-nimbus-dev-namespace" }, TimeSpan.Zero, "testing", CancellationToken.None);

        Assert.Contains(azureCli.Commands, c => c.Contains("private-endpoint", StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunIfPrivateAsync_StopsAPrivateDeploymentThatThisMachineCannotReach()
    {
        var azureCli = NetworkWithRecordedState("private");

        var error = await Assert.ThrowsAsync<CommandException>(() => PrivateNetworkPreflight.RunIfPrivateAsync(
            azureCli, UnreachableCheck(azureCli), "rg", new[] { "pe-sb-nimbus-dev-namespace" }, TimeSpan.Zero, "provisioning the Service Bus topology", CancellationToken.None));

        Assert.Contains("provisioning the Service Bus topology", error.Message, StringComparison.Ordinal);
        Assert.Contains(Namespace, error.Message, StringComparison.Ordinal);
    }

    private static RecordingAzureCliRunner NetworkWithRecordedState(string? recordedMode) => new()
    {
        Responder = arguments =>
            arguments.Contains("group") ? (recordedMode is null ? "null" : $$"""{"nimbus-network-mode":"{{recordedMode}}"}""")
            : arguments.Contains("private-endpoint") ? "/subscriptions/x/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/pe-nic"
            : arguments.Contains("nic") ? $$"""[{"ip":"{{EndpointIp}}","fqdns":["{{Namespace}}"]}]"""
            : null,
    };

    private static PrivateEndpointDnsCheck UnreachableCheck(RecordingAzureCliRunner azureCli) => new(
        azureCli,
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("20.38.116.190") }),
        (_, _) => Task.CompletedTask);

    private static ExpectedEndpoint Expected() => new(Namespace, EndpointIp, "pe-sb-nimbus-dev-namespace");

    private static PrivateEndpointDnsCheck Check(Func<string, IPAddress[]?> answer, FakeClock clock) =>
        new(
            new RecordingAzureCliRunner(),
            (host, _) => answer(host) is { } addresses
                ? Task.FromResult(addresses)
                : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            clock.DelayAsync);

    private sealed class FakeClock
    {
        public TimeSpan Waited { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Waited += delay;
            return Task.CompletedTask;
        }
    }
}

using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using CoreProvisioner = NimBus.ServiceBus.Provisioning.ServiceBusTopologyProvisioner;

namespace NimBus.CommandLine;

/// <summary>
/// CLI-side wrapper for <see cref="CoreProvisioner"/>: resolves the namespace connection
/// string via the Azure CLI for the built-in <see cref="PlatformConfiguration"/> and then
/// delegates the actual topology work to the shared provisioning library.
/// </summary>
internal sealed class ServiceBusTopologyProvisioner
{
    private readonly AzureCliRunner _az;
    private readonly Func<TopologyOptions, CancellationToken, Task<string>> _connectionStringProvider;
    private readonly Func<string, ServiceBusAdministrationClient> _clientFactory;
    private readonly Func<IPlatform> _platformFactory;

    internal ServiceBusTopologyProvisioner(AzureCliRunner az)
        : this(
            az,
            static (options, cancellationToken, runner) => ReadConnectionStringAsync(runner, options, cancellationToken),
            static connectionString => new ServiceBusAdministrationClient(connectionString),
            static () => new PlatformConfiguration())
    {
    }

    internal ServiceBusTopologyProvisioner(
        AzureCliRunner az,
        Func<TopologyOptions, CancellationToken, AzureCliRunner, Task<string>> connectionStringProvider,
        Func<string, ServiceBusAdministrationClient> clientFactory,
        Func<IPlatform> platformFactory)
    {
        _az = az;
        _connectionStringProvider = (options, cancellationToken) => connectionStringProvider(options, cancellationToken, _az);
        _clientFactory = clientFactory;
        _platformFactory = platformFactory;
    }

    internal async Task ApplyAsync(TopologyOptions options, CancellationToken cancellationToken)
    {
        var connectionString = await _connectionStringProvider(options, cancellationToken).ConfigureAwait(false);

        var core = new CoreProvisioner(
            _clientFactory(connectionString),
            _platformFactory,
            CoreProvisioner.IsEmulator(connectionString),
            CliOutput.WriteLine);

        await core.ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadConnectionStringAsync(AzureCliRunner az, TopologyOptions options, CancellationToken cancellationToken)
    {
        var names = NamingConventions.Build(options.SolutionId, options.Environment);

        await az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        return await az.CaptureValueAsync(
            new[]
            {
                "servicebus", "namespace", "authorization-rule", "keys", "list",
                "--resource-group", options.ResourceGroupName,
                "--namespace-name", names.ServiceBusNamespace,
                "--name", "RootManageSharedAccessKey",
                "--query", "primaryConnectionString",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Service Bus connection string for '{names.ServiceBusNamespace}'.").ConfigureAwait(false);
    }
}

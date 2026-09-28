using DynamicsBcDemo.Contracts;
using NimBus.ServiceBus.Provisioning;

// Creates the topics, forwarding subscriptions, reply subscription and deferred subscriptions for
// D365SalesEndpoint and BusinessCentralEndpoint. Runs the same against the NimBus Service Bus
// emulator and real Azure Service Bus, is idempotent, and fails provisioning if the catalog
// declares a command with other than exactly one consumer. The AppHost holds every other
// resource back (WaitForCompletion) until this exits successfully.
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException("ConnectionStrings__servicebus is required.");

var provisioner = new ServiceBusTopologyProvisioner(connectionString, () => new DynamicsBcPlatformConfiguration());

Console.WriteLine("Provisioning the Dynamics 365 Sales / Business Central demo topology...");
await provisioner.ApplyAsync(CancellationToken.None);
Console.WriteLine("Topology provisioning complete.");

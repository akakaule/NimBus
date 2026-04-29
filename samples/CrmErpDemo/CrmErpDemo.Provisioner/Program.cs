using CrmErpDemo.Contracts;
using NimBus.CommandLine;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException("ConnectionStrings__servicebus is required.");

var provisioner = new ServiceBusTopologyProvisioner(connectionString, () => new CrmErpPlatformConfiguration());

Console.WriteLine("Provisioning CRM/ERP demo Service Bus topology...");
await provisioner.ApplyAsync(CancellationToken.None);
Console.WriteLine("Topology provisioning complete.");

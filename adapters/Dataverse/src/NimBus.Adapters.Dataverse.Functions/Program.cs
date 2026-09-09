using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.Adapters.Dataverse;
using NimBus.SDK.Extensions;

var builder = FunctionsApplication.CreateBuilder(args);
var options = new DataverseOptions();
builder.Configuration.GetSection("Dataverse").Bind(options);
options.Validate();
var outputNamespace = builder.Configuration["NimBusServiceBus:fullyQualifiedNamespace"]
    ?? throw new InvalidOperationException("NimBusServiceBus__fullyQualifiedNamespace is required.");
builder.Services.AddSingleton(_ => new ServiceBusClient(outputNamespace, new DefaultAzureCredential()));
builder.Services.AddNimBusPublisher(options.PublisherEndpoint);
builder.Services.AddDataverseAdapter(options);
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();
await builder.Build().RunAsync().ConfigureAwait(false);

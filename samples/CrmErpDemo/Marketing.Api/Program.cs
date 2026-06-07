using Marketing.Api;
using Marketing.Api.Endpoints;
using NimBus.SDK;
using NimBus.SDK.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Only wire the real ServiceBus client if a connection string was provided.
// Without this guard, Aspire.Azure.Messaging.ServiceBus throws at startup when
// ConnectionStrings:servicebus is missing, Kestrel never binds, and callers
// see ECONNREFUSED. With the guard: the API still comes up and returns 200 on
// /api/leads/ping; publishing to a real topic requires Service Bus.
var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
    builder.Services.AddNimBusPublisher("MarketingEndpoint");
}
else
{
    builder.Services.AddSingleton<IPublisherClient, NoopPublisherClient>();
}

// Schema seeder: registers marketing.lead.created.v1 and erp.customer.upsert.v1
// in the NimBus schema registry via the agent REST API on nimbus-ops at startup.
// The seeder retries until nimbus-ops is reachable (Aspire start-order dependency).
// Base address uses Aspire service-discovery so "https+http://nimbus-ops" is
// rewritten to the resolved endpoint at send time.
builder.Services.AddHttpClient<SchemaSeeder>(client =>
{
    client.BaseAddress = new Uri("https+http://nimbus-ops");
    client.DefaultRequestHeaders.Add("X-Agent-Id", "marketing-api");
});
builder.Services.AddHostedService<SchemaSeeder>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.MapDefaultEndpoints();

app.MapLeadEndpoints();

app.Run();

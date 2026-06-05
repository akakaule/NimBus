using EnrichmentAgent;
using EnrichmentAgent.Bus;
using EnrichmentAgent.Classification;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// DI: select Claude classifier when ANTHROPIC_API_KEY is available, otherwise
// fall back to the deterministic fake (used in CI, in-memory tests, local dev
// without a key).
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    builder.Services.AddSingleton<IContactClassifier, ClaudeContactClassifier>();
else
    builder.Services.AddSingleton<IContactClassifier, DeterministicContactClassifier>();

// Typed HttpClient over the Phase-1 agent REST API. The base address uses Aspire's
// service-discovery scheme to resolve the nimbus-ops WebApp resource — AddServiceDefaults
// wires the service-discovery handler onto every HttpClient, so "https+http://nimbus-ops"
// is rewritten to the resolved endpoint at send time. X-Agent-Id is the demo-grade
// identity sent on every request (matches AgentImplementation's expected header).
builder.Services.AddHttpClient<IBusGateway, RestBusGateway>(client =>
{
    client.BaseAddress = new Uri("https+http://nimbus-ops");
    client.DefaultRequestHeaders.Add("X-Agent-Id", "enrichment-agent");
});

// The receive -> classify -> define -> publish -> settle loop.
builder.Services.AddHostedService<AgentLoopWorker>();

var host = builder.Build();
host.Run();

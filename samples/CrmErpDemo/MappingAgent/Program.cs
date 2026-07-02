using MappingAgent;
using MappingAgent.Authoring;
using MappingAgent.Bus;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// DI: select Claude author when ANTHROPIC_API_KEY is available, otherwise fall back to the
// deterministic author (used in CI, local dev without a key, smoke tests).
// Mirrors Program.cs in the EnrichmentAgent.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    builder.Services.AddSingleton<IMappingAuthor, ClaudeMappingAuthor>();
else
    builder.Services.AddSingleton<IMappingAuthor, DeterministicMappingAuthor>();

// Typed HttpClient over the NimBus agent REST API. Base address uses Aspire service-discovery
// to resolve nimbus-ops; X-Agent-Id identifies the agent on every request.
// Mirrors RestBusGateway registration in EnrichmentAgent.
builder.Services.AddHttpClient<IMappingBusGateway, RestMappingGateway>(client =>
{
    client.BaseAddress = new Uri("https+http://nimbus-ops");
    client.DefaultRequestHeaders.Add("X-Agent-Id", "mapping-agent");
});

// The author loop: read schemas → author transform → submit proposal.
builder.Services.AddHostedService<MappingAgentLoopWorker>();

var host = builder.Build();
host.Run();

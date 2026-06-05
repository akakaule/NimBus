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

// TODO(Task F): register the NimBus subscriber + receiver for CrmEndpoint here,
// add the IEventHandler<CrmContactCreated> that calls IContactClassifier.Classify()
// and publishes crm.contact.enriched.v1 back through the sender.

var host = builder.Build();
host.Run();

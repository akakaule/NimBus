using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Mcp.Configuration;
using NimBus.Mcp.Http;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout = JSON-RPC

// ── Options: env-vars override appsettings ────────────────────────────────────
// Env vars NIMBUS_API_BASEURL and NIMBUS_AGENT_ID are mapped to the NimBus config section.
// CreateApplicationBuilder already reads environment variables; we apply a manual override
// so NIMBUS_* env vars shadow the appsettings "NimBus" section values.
var options = new NimBusMcpOptions();
// Read appsettings "NimBus" section values as fallback defaults.
var section = builder.Configuration.GetSection("NimBus");
var sectionBaseUrl = section["BaseUrl"];
var sectionAgentId = section["AgentId"];
if (!string.IsNullOrWhiteSpace(sectionBaseUrl)) options.BaseUrl = sectionBaseUrl;
if (!string.IsNullOrWhiteSpace(sectionAgentId)) options.AgentId = sectionAgentId;

// Env-var override (NIMBUS_API_BASEURL / NIMBUS_AGENT_ID)
var envBaseUrl = builder.Configuration["NIMBUS_API_BASEURL"];
var envAgentId = builder.Configuration["NIMBUS_AGENT_ID"];
if (!string.IsNullOrWhiteSpace(envBaseUrl)) options.BaseUrl = envBaseUrl;
if (!string.IsNullOrWhiteSpace(envAgentId)) options.AgentId = envAgentId;

// Validate BaseUrl is an absolute URI before the host starts.
if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
{
    throw new InvalidOperationException(
        $"NimBus MCP configuration error: BaseUrl '{options.BaseUrl}' is not a valid absolute URI. " +
        "Set NIMBUS_API_BASEURL to an absolute URL, e.g. http://localhost:5000.");
}

// Make the validated options available to services.
builder.Services.AddSingleton(options);

// ── Typed HTTP client ─────────────────────────────────────────────────────────
builder.Services.AddHttpClient<INimBusAgentApi, NimBusAgentApiClient>((_, client) =>
{
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Add("X-Agent-Id", options.AgentId);
});

// ── MCP server ────────────────────────────────────────────────────────────────
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

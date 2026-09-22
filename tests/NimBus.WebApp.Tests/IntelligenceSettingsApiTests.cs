#pragma warning disable CA1707, CA2007
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceSettingsApiTests
{
    [TestMethod]
    public async Task Api_Requires_SiteOwner_Csrf_Consent_And_Current_Revision()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();
        using var host = await CreateHost(store, audit);
        using var client = host.GetTestClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/failure-intelligence")).StatusCode);
        client.DefaultRequestHeaders.Add("TestRole", "Reader");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/failure-intelligence")).StatusCode);
        Assert.AreEqual(0, store.Reads);
        client.DefaultRequestHeaders.Remove("TestRole");
        client.DefaultRequestHeaders.Add("TestRole", "Owner");
        var response = await client.GetAsync("/api/admin/failure-intelligence");
        var text = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(text.Contains("deployment-secret", StringComparison.Ordinal));
        var state = JsonNode.Parse(text)!;
        var update = new SaveIntelligenceSettings { Revision = "none", Settings = new() { Enabled = true, IncludeEventPayload = true } };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", update)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", update)).StatusCode);
        update.PayloadSharingAcknowledged = true;
        var saved = await client.PutAsJsonAsync("/api/admin/failure-intelligence", update);
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var savedState = JsonNode.Parse(await saved.Content.ReadAsStringAsync())!;
        Assert.IsTrue(savedState["restartRequired"]!.GetValue<bool>());
        Assert.IsFalse(savedState["active"]!["includeEventPayload"]!.GetValue<bool>());
        Assert.IsTrue(savedState["saved"]!["includeEventPayload"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", update)).StatusCode);
        Assert.AreEqual(1, store.Writes);
        Assert.AreEqual(4, audit.Writes);
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var restarted = await IntelligenceSettingsBootstrap.LoadAsync(configuration, store, CancellationToken.None);
        Assert.IsTrue(IntelligenceAdminSettings.FromConfiguration(restarted).IncludeEventPayload);
        Assert.AreEqual(store.Document!.Revision, restarted[IntelligenceSettingsBootstrap.RevisionKey]);
    }

    [TestMethod]
    public async Task Storage_Failure_Is_Sanitized_And_Unknown_Fields_Are_Rejected()
    {
        var store = new FakeStore();
        using var host = await CreateHost(store, new FakeAudit());
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("TestRole", "Owner");
        var response = await client.GetAsync("/api/admin/failure-intelligence");
        var state = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());
        var body = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new SaveIntelligenceSettings { Settings = new(), Revision = "none" }))!;
        body["BaseUrl"] = "https://must-not-be-stored.example";
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", body)).StatusCode);
        Assert.AreEqual(0, store.Writes);
        store.Throw = true;
        response = await client.GetAsync("/api/admin/failure-intelligence");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("secret", StringComparison.Ordinal));
        var failed = IntelligenceSettingsBootstrap.FailClosed(host.Services.GetRequiredService<IConfiguration>());
        Assert.IsFalse(failed.GetValue<bool>("NimBus:IntegrationIntelligence:Enabled"));
    }

    [TestMethod]
    public async Task Api_Saves_Provider_Key_Sealed_Never_Echoes_It_And_Applies_It_After_Restart()
    {
        const string plaintext = "ts-live-key-1234567890";
        const string url = "/api/admin/failure-intelligence";
        var store = new FakeStore();
        var audit = new FakeAudit();
        using var host = await CreateHost(store, audit);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("TestRole", "Owner");
        var response = await client.GetAsync(url);
        var state = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("deployment", state["credentialSource"]!.GetValue<string>());
        Assert.AreEqual("none", state["savedApiKey"]!.GetValue<string>());
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());

        // Rejected shapes never reach the store: key together with clear, whitespace inside, too long.
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url,
            new SaveIntelligenceSettings { Revision = "none", Settings = new(), ApiKey = plaintext, ClearApiKey = true })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url,
            new SaveIntelligenceSettings { Revision = "none", Settings = new(), ApiKey = "has space" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url,
            new SaveIntelligenceSettings { Revision = "none", Settings = new(), ApiKey = new string('k', 513) })).StatusCode);
        Assert.AreEqual(0, store.Writes);

        // Save: sealed at rest, reported as configured, never echoed, audited without the value.
        var saved = await client.PutAsJsonAsync(url,
            new SaveIntelligenceSettings { Revision = "none", Settings = new(), ApiKey = "  " + plaintext + "  " });
        var text = await saved.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, text);
        Assert.IsFalse(text.Contains(plaintext, StringComparison.Ordinal));
        var savedState = JsonNode.Parse(text)!;
        Assert.AreEqual("configured", savedState["savedApiKey"]!.GetValue<string>());
        Assert.AreEqual("deployment", savedState["credentialSource"]!.GetValue<string>(), "The running instance keeps its startup credential until restart.");
        Assert.IsNotNull(store.Document!.ProtectedApiKey);
        Assert.AreNotEqual(plaintext, store.Document.ProtectedApiKey);
        Assert.IsFalse(Newtonsoft.Json.JsonConvert.SerializeObject(store.Document).Contains(plaintext, StringComparison.Ordinal));
        Assert.IsTrue(audit.Data.Last()!.Contains("\"apiKey\":\"replaced\"", StringComparison.Ordinal), audit.Data.Last());
        Assert.IsFalse(audit.Data.Last()!.Contains(plaintext, StringComparison.Ordinal));

        // Omitting the key carries the sealed value forward unchanged.
        var sealedKey = store.Document.ProtectedApiKey;
        saved = await client.PutAsJsonAsync(url,
            new SaveIntelligenceSettings { Revision = store.Document.Revision, Settings = new() { Enabled = true } });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        Assert.AreEqual(sealedKey, store.Document.ProtectedApiKey);
        Assert.IsTrue(audit.Data.Last()!.Contains("\"apiKey\":\"unchanged\"", StringComparison.Ordinal));

        // Restart: the saved key overrides the deployment key and the snapshot reports its source.
        var protector = host.Services.GetRequiredService<IIntelligenceSecretProtector>();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var restarted = await IntelligenceSettingsBootstrap.LoadAsync(configuration, store, protector, CancellationToken.None);
        Assert.AreEqual(plaintext, restarted["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        Assert.AreEqual("saved", IntelligenceSettingsSnapshot.Create(restarted).CredentialSource);
        Assert.IsTrue(IntelligenceSettingsSnapshot.Create(restarted).CredentialConfigured);
        Assert.IsFalse(Newtonsoft.Json.JsonConvert.SerializeObject(IntelligenceAdminSettings.FromConfiguration(restarted)).Contains(plaintext, StringComparison.Ordinal));

        // Clear: the deployment key applies again after restart.
        saved = await client.PutAsJsonAsync(url,
            new SaveIntelligenceSettings { Revision = store.Document.Revision, Settings = new(), ClearApiKey = true });
        text = await saved.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, text);
        Assert.IsNull(store.Document.ProtectedApiKey);
        Assert.AreEqual("none", JsonNode.Parse(text)!["savedApiKey"]!.GetValue<string>());
        Assert.IsTrue(audit.Data.Last()!.Contains("\"apiKey\":\"cleared\"", StringComparison.Ordinal));
        restarted = await IntelligenceSettingsBootstrap.LoadAsync(configuration, store, protector, CancellationToken.None);
        Assert.AreEqual("deployment-secret", restarted["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        Assert.AreEqual("deployment", IntelligenceSettingsSnapshot.Create(restarted).CredentialSource);

        // A key sealed by another key ring is reported as unreadable and falls back to deployment without failing closed.
        store.Seed(new IntelligenceSettingsDocument(new() { Enabled = true }, Guid.NewGuid().ToString("D"),
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()).Protect(plaintext)));
        response = await client.GetAsync(url);
        Assert.AreEqual("unreadable", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["savedApiKey"]!.GetValue<string>());
        restarted = await IntelligenceSettingsBootstrap.LoadAsync(configuration, store, protector, CancellationToken.None);
        Assert.AreEqual("deployment-secret", restarted["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        Assert.IsTrue(IntelligenceAdminSettings.FromConfiguration(restarted).Enabled, "Other saved settings still apply.");
        Assert.IsFalse(restarted.GetValue<bool>(IntelligenceSettingsBootstrap.FailureKey));
    }

    private static Task<IHost> CreateHost(FakeStore store, FakeAudit audit) => new HostBuilder()
        .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
        { ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "deployment-secret" }))
        .ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting(); services.AddAuthorization();
            services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
            services.AddSingleton<IIntelligenceSettingsStore>(store); services.AddSingleton<IAuditLogService>(audit);
            services.AddSingleton<IIntelligenceSecretProtector>(new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
            services.AddSingleton(sp => IntelligenceSettingsSnapshot.Create(sp.GetRequiredService<IConfiguration>()));
            services.AddHttpContextAccessor(); services.AddScoped<IEndpointAuthorizationService, FakeAuthorization>();
            services.AddControllers().AddApplicationPart(typeof(IntelligenceSettingsController).Assembly);
        }).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                var role = context.Request.Headers["TestRole"].ToString();
                if (role.Length == 0) { context.Response.StatusCode = 401; return; }
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test"), new Claim(ClaimTypes.Role, role)], "test"));
                await next(context);
            });
            app.UseRouting(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers());
        })).StartAsync();

    private sealed class FakeAuthorization(IHttpContextAccessor accessor) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(accessor.HttpContext!.User.IsInRole("Owner"));
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => throw new NotSupportedException();
        public string? GetCurrentUserName() => "test";
    }
    private sealed class FakeAudit : IAuditLogService
    {
        public int Writes { get; private set; }
        public List<string?> Data { get; } = [];
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default) { Writes++; Data.Add(data); return Task.CompletedTask; }
    }
    private sealed class FakeStore : IIntelligenceSettingsStore
    {
        public IntelligenceSettingsDocument? Document { get; private set; }
        public void Seed(IntelligenceSettingsDocument document) => Document = document;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool Throw { get; set; }
        public Task<IntelligenceSettingsDocument?> ReadAsync(CancellationToken cancellationToken)
        { Reads++; if (Throw) throw new InvalidOperationException("secret connection details"); return Task.FromResult(Document); }
        public Task<bool> TrySaveAsync(IntelligenceSettingsDocument document, string expectedRevision, CancellationToken cancellationToken)
        {
            if ((Document?.Revision ?? "none") != expectedRevision) return Task.FromResult(false);
            Document = document; Writes++; return Task.FromResult(true);
        }
    }
}

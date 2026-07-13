#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class AdminStatusSafetyTests
{
    private const string EndpointId = "endpoint-a";
    private const string EventId = "event-a";
    private const string SessionId = "session-a";
    private static readonly string[] TerminalStatusNames =
    {
        nameof(StoreResolutionStatus.Completed),
        nameof(StoreResolutionStatus.Skipped),
    };

    [TestMethod]
    [DataRow("delete-by-status", "{}")]
    [DataRow("delete-by-status", "{\"statuses\":null}")]
    [DataRow("delete-by-status", "{\"statuses\":[]}")]
    [DataRow("delete-by-status", "{\"statuses\":[\"NotAStatus\"]}")]
    [DataRow("delete-by-status", "{\"statuses\":[999]}")]
    [DataRow("skip", "{}")]
    [DataRow("skip", "{\"statuses\":null}")]
    [DataRow("skip", "{\"statuses\":[]}")]
    [DataRow("skip", "{\"statuses\":[\"Completed\"]}")]
    [DataRow("skip", "{\"statuses\":[\"Skipped\"]}")]
    public async Task Authenticated_http_request_with_invalid_statuses_returns_bad_request_without_mutation(
        string operation,
        string json)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);

        using var host = await CreateHost(store);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/endpoint/{EndpointId}/{operation}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        using var client = host.GetTestServer().CreateClient();
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored.ResolutionStatus);
    }

    [TestMethod]
    public async Task Authenticated_http_request_with_valid_skip_status_updates_matching_event()
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);

        using var host = await CreateHost(store);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/endpoint/{EndpointId}/skip")
        {
            Content = new StringContent(
                "{\"statuses\":[\"Failed\"]}",
                Encoding.UTF8,
                "application/json"),
        };
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Skipped, stored.ResolutionStatus);
    }

    [TestMethod]
    public async Task Controller_rejects_null_delete_statuses_before_calling_service()
    {
        var service = new ThrowingAdminService();
        var sut = CreateController(service);

        var result = await sut.PostAdminDeleteByStatusAsync(
            EndpointId,
            new DeleteByStatusRequest { Statuses = null! });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, service.CallCount);
    }

    [TestMethod]
    public async Task Controller_rejects_empty_delete_statuses_before_calling_service()
    {
        var service = new ThrowingAdminService();
        var sut = CreateController(service);

        var result = await sut.PostAdminDeleteByStatusPreviewAsync(
            EndpointId,
            new DeleteByStatusRequest { Statuses = new List<AdminDeleteStatus>() });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, service.CallCount);
    }

    [TestMethod]
    public async Task Controller_rejects_empty_skip_statuses_before_calling_service()
    {
        var service = new ThrowingAdminService();
        var sut = CreateController(service);

        var result = await sut.PostAdminSkipAsync(
            EndpointId,
            new SkipRequest { Statuses = new List<AdminSkipSourceStatus>() });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, service.CallCount);
    }

    [TestMethod]
    [DataRow("delete-preview")]
    [DataRow("delete")]
    [DataRow("skip-preview")]
    [DataRow("skip")]
    public async Task Service_boundary_rejects_empty_statuses_without_mutating_event(string operation)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => InvokeOperation(sut, operation, new List<string>()));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored.ResolutionStatus);
    }

    [TestMethod]
    [DataRow("NotAStatus")]
    [DataRow("1")]
    public async Task Service_boundary_rejects_unknown_delete_status_without_mutating_event(string status)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.DeleteByStatusAsync(EndpointId, new List<string> { status }));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored.ResolutionStatus);
    }

    [TestMethod]
    public async Task Service_boundary_rejects_null_statuses_without_mutating_event()
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.DeleteByStatusAsync(EndpointId, null!));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.SkipMessagesAsync(EndpointId, null!, before: null));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored.ResolutionStatus);
    }

    [TestMethod]
    [DataRow(nameof(StoreResolutionStatus.Failed))]
    [DataRow(nameof(StoreResolutionStatus.Deferred))]
    [DataRow(nameof(StoreResolutionStatus.DeadLettered))]
    [DataRow(nameof(StoreResolutionStatus.Unsupported))]
    [DataRow(nameof(StoreResolutionStatus.Pending))]
    [DataRow(nameof(StoreResolutionStatus.TooManyRequests))]
    [DataRow(nameof(StoreResolutionStatus.Published))]
    public void Skip_validation_accepts_each_supported_source_status(string status)
    {
        var valid = AdminStatusValidation.TryNormalizeSkipStatuses(
            new[] { status },
            out var normalized,
            out var error);

        Assert.IsTrue(valid, error);
        CollectionAssert.AreEqual(new[] { status }, normalized);
    }

    [TestMethod]
    public void Generated_admin_status_contract_matches_message_store_status_rules()
    {
        var allStoreStatuses = Enum.GetNames<StoreResolutionStatus>();
        var skippableStoreStatuses = allStoreStatuses
            .Except(TerminalStatusNames, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(allStoreStatuses, Enum.GetNames<AdminDeleteStatus>());
        CollectionAssert.AreEquivalent(skippableStoreStatuses, Enum.GetNames<AdminSkipSourceStatus>());
    }

    [TestMethod]
    [DataRow(nameof(StoreResolutionStatus.Completed))]
    [DataRow(nameof(StoreResolutionStatus.Skipped))]
    public async Task Service_boundary_rejects_terminal_status_without_mutating_event(string status)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.SkipMessagesAsync(
                EndpointId,
                new List<string> { status },
                before: null));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored.ResolutionStatus);
    }

    private static Task InvokeOperation(AdminService service, string operation, List<string> statuses) =>
        operation switch
        {
            "delete-preview" => AsTask(service.DeleteByStatusPreviewAsync(EndpointId, statuses)),
            "delete" => service.DeleteByStatusAsync(EndpointId, statuses),
            "skip-preview" => AsTask(service.SkipMessagesPreviewAsync(EndpointId, statuses, before: null)),
            "skip" => service.SkipMessagesAsync(EndpointId, statuses, before: null),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

    private static async Task AsTask<T>(Task<T> task) => await task;

    private static async Task SeedFailedEvent(InMemoryMessageStore store)
    {
        await store.UploadFailedMessage(
            EventId,
            SessionId,
            EndpointId,
            new UnresolvedEvent
            {
                EventId = EventId,
                SessionId = SessionId,
                EndpointId = EndpointId,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
    }

    private static async Task<IHost> CreateHost(InMemoryMessageStore store)
    {
        var platform = new FakePlatform(EndpointId);
        var adminService = CreateAdminService(store);

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatform>(platform);
                    services.AddSingleton<IAdminService>(adminService);
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IAuditLogService, NoOpAuditLogService>();
                    services.AddScoped<IAdminApiController, AdminImplementation>();
                    services.AddControllers()
                        .AddApplicationPart(typeof(AdminApiController).Assembly)
                        .AddJsonOptions(options =>
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        var identity = new ClaimsIdentity(
                            new[] { new Claim("groups", "EIP_Management") },
                            authenticationType: "Test");
                        context.User = new ClaimsPrincipal(identity);
                        await next();
                    });
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        return await builder.StartAsync();
    }

    private static AdminImplementation CreateController(IAdminService service)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("groups", "EIP_Management") },
                authenticationType: "Test")),
        };

        return new AdminImplementation(
            new HttpContextAccessor { HttpContext = context },
            service,
            new FakePlatform(EndpointId),
            new ConfigurationBuilder().Build(),
            new NoOpAuditLogService());
    }

    private static AdminService CreateAdminService(InMemoryMessageStore store) =>
        new(
            platform: null!,
            cosmosClient: store,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: null!,
            managerClient: null!,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null);

    private sealed class ThrowingAdminService : IAdminService
    {
        public int CallCount { get; private set; }

        private Task<T> Unexpected<T>()
        {
            CallCount++;
            throw new AssertFailedException("The controller called IAdminService for invalid input.");
        }

        public Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform) => Unexpected<PlatformConfig>();
        public Task<TopologyAuditResult> AuditTopologyAsync(string endpointName) => Unexpected<TopologyAuditResult>();
        public Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName) => Unexpected<TopologyCleanupResult>();
        public Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId) => Unexpected<BulkResubmitPreview>();
        public Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<int> GetDeadLetteredCountAsync(string endpointId) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId) => Unexpected<SessionPurgePreview>();
        public Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId) => Unexpected<SessionPurgeResult>();
        public Task<bool> DeleteEventAsync(string endpointId, string eventId) => Unexpected<bool>();
        public Task<BulkOperationResult> DeleteAllEventsAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<PurgePreview> PurgeSubscriptionPreviewAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<PurgePreview>();
        public Task<BulkOperationResult> PurgeSubscriptionAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteMessagesByToPreviewAsync(string toField) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteMessagesByToAsync(string toField) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteByStatusPreviewAsync(string endpointId, List<string> statuses) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteByStatusAsync(string endpointId, List<string> statuses) => Unexpected<BulkOperationResult>();
        public Task<int> SkipMessagesPreviewAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<int>();
        public Task<BulkOperationResult> SkipMessagesAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<CopyResult> CopyEndpointDataAsync(string endpointId, string targetConnectionString, DateTime? from, DateTime? to, List<string> statuses, int? batchSize) => Unexpected<CopyResult>();
        public Task<DeferredReprocessResult> ReprocessDeferredAsync(string endpointId, string sessionId) => Unexpected<DeferredReprocessResult>();
    }

    private sealed class NoOpAuditLogService : IAuditLogService
    {
        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePlatform : IPlatform
    {
        public FakePlatform(string endpointId) => Endpoints = new[] { new FakeEndpoint(endpointId) };

        public IEnumerable<IEndpoint> Endpoints { get; }
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id) => Id = id;

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }
}

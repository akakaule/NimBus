#pragma warning disable CA1707, CA2007

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Endpoints;
using NimBus.MessageStore;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Endpoint = NimBus.Core.Endpoints.Endpoint;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class AdminCosmosContainerTests
{
    [TestMethod]
    public async Task List_returns_only_containers_outside_platform()
    {
        var cosmos = new RecordingContainerAdmin(
            "CurrentEndpoint", "currentendpoint", "messages", "Messages", "orphan-a");
        var sut = CreateController(cosmos, authorized: true);

        var response = await sut.GetAdminCosmosContainersAsync();

        var result = Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        var names = Assert.IsInstanceOfType<IEnumerable<CosmosContainerInfo>>(result.Value)
            .Select(container => container.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Messages", "currentendpoint", "orphan-a" }, names);
    }

    [TestMethod]
    public async Task Delete_requires_owner_and_audits_denial()
    {
        var cosmos = new RecordingContainerAdmin("orphan-a");
        var audit = new RecordingAuditLogService();
        var sut = CreateController(cosmos, authorized: false, audit);

        var response = await sut.PostAdminCosmosContainerDeleteAsync(
            new CosmosContainerDeleteRequest { Confirmation = "orphan-a" }, "orphan-a");

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        Assert.AreEqual(0, cosmos.DeleteCalls);
        Assert.AreEqual(1, audit.Entries.Count);
        Assert.IsTrue(audit.Entries[0].AccessDenied);
    }

    [TestMethod]
    [DataRow("CurrentEndpoint")]
    [DataRow("messages")]
    public async Task Delete_refuses_platform_and_internal_containers(string name)
    {
        var cosmos = new RecordingContainerAdmin(name);
        var response = await CreateController(cosmos, authorized: true)
            .PostAdminCosmosContainerDeleteAsync(
                new CosmosContainerDeleteRequest { Confirmation = name }, name);

        Assert.IsInstanceOfType<BadRequestObjectResult>(response.Result);
        Assert.AreEqual(0, cosmos.DeleteCalls);
    }

    [TestMethod]
    public async Task Delete_requires_exact_confirmation_and_current_listing()
    {
        var cosmos = new RecordingContainerAdmin("orphan-a");
        var sut = CreateController(cosmos, authorized: true);

        var mismatch = await sut.PostAdminCosmosContainerDeleteAsync(
            new CosmosContainerDeleteRequest { Confirmation = "ORPHAN-A" }, "orphan-a");
        var missing = await sut.PostAdminCosmosContainerDeleteAsync(
            new CosmosContainerDeleteRequest { Confirmation = "missing" }, "missing");

        Assert.IsInstanceOfType<BadRequestObjectResult>(mismatch.Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>(missing.Result);
        Assert.AreEqual(0, cosmos.DeleteCalls);
    }

    [TestMethod]
    public async Task Delete_orphaned_container_and_audit_success()
    {
        var cosmos = new RecordingContainerAdmin("orphan-a");
        var audit = new RecordingAuditLogService();
        var response = await CreateController(cosmos, authorized: true, audit)
            .PostAdminCosmosContainerDeleteAsync(
                new CosmosContainerDeleteRequest { Confirmation = "orphan-a" }, "orphan-a");

        var result = Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        var body = Assert.IsInstanceOfType<CosmosContainerDeleteResult>(result.Value);
        Assert.IsTrue(body.Deleted);
        Assert.AreEqual("orphan-a", body.Name);
        Assert.AreEqual(1, cosmos.DeleteCalls);
        Assert.AreEqual(MessageAuditType.DeleteStorageContainer, audit.Entries.Single().Type);
    }

    private static AdminImplementation CreateController(
        ICosmosContainerAdmin? cosmos,
        bool authorized,
        RecordingAuditLogService? audit = null)
    {
        var context = new DefaultHttpContext();
        return new AdminImplementation(
            new TestHttpContextAccessor(context), null!, null!, new TestPlatform(),
            new ConfigurationBuilder().Build(), audit ?? new RecordingAuditLogService(),
            new FixedAuthorizationService(authorized), null!, containerAdmin: cosmos);
    }

    private sealed class TestPlatform : NimBus.Core.Platform
    {
        public TestPlatform() => AddEndpoint(new CurrentEndpoint());
    }

    private sealed class CurrentEndpoint : Endpoint { }

    private sealed class TestHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class FixedAuthorizationService(bool authorized) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(authorized);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string? GetCurrentUserName() => "test-owner";
    }

    private sealed class RecordingContainerAdmin(params string[] containers) : ICosmosContainerAdmin
    {
        private readonly List<string> _containers = [.. containers];
        public int DeleteCalls { get; private set; }
        public Task<IReadOnlyList<string>> ListContainerIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_containers);
        public Task<bool> DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(_containers.Remove(containerId));
        }
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<(MessageAuditType Type, bool AccessDenied, string Data)> Entries { get; } = [];
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false,
            string? data = null, string? eventId = null, string? endpointId = null,
            string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((type, accessDenied, data ?? string.Empty));
            return Task.CompletedTask;
        }
    }
}

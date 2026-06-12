#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Hubs;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Covers the webhook-key gate on the anonymous storage-hook endpoint: the key
/// is accepted from the X-Webhook-Key header (preferred) or the legacy ?key=
/// query parameter, compared in fixed time, and fails closed outside
/// Development when unconfigured.
/// </summary>
[TestClass]
public class StorageHookWebhookKeyTests
{
    private const string ConfiguredKey = "test-webhook-key-123";
    private const string KnownEndpointId = "ep-1";

    [TestMethod]
    public async Task Receive_accepts_key_from_header_and_pushes_update()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[StorageHookImplementation.WebhookKeyHeaderName] = ConfiguredKey;
        var proxy = new RecordingClientProxy();
        var sut = CreateSut(ConfiguredKey, Environments.Production, context, proxy);

        var result = await sut.StoragehookReceiveAsync(KnownEndpointId);

        Assert.IsInstanceOfType(result, typeof(OkResult));
        Assert.AreEqual(1, proxy.SentMethods.Count);
    }

    [TestMethod]
    public async Task Receive_accepts_legacy_key_from_query_string()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?key={ConfiguredKey}");
        var sut = CreateSut(ConfiguredKey, Environments.Production, context, new RecordingClientProxy());

        var result = await sut.StoragehookReceiveAsync(KnownEndpointId);

        Assert.IsInstanceOfType(result, typeof(OkResult));
    }

    [TestMethod]
    public async Task Receive_rejects_wrong_key()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[StorageHookImplementation.WebhookKeyHeaderName] = "wrong-key";
        var sut = CreateSut(ConfiguredKey, Environments.Production, context, new RecordingClientProxy());

        var result = await sut.StoragehookReceiveAsync(KnownEndpointId);

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
    }

    [TestMethod]
    public async Task Receive_rejects_missing_key()
    {
        var sut = CreateSut(ConfiguredKey, Environments.Production, new DefaultHttpContext(), new RecordingClientProxy());

        var result = await sut.StoragehookReceiveAsync(KnownEndpointId);

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
    }

    [TestMethod]
    public async Task Receive_fails_closed_when_key_unconfigured_outside_development()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?key=anything");
        var sut = CreateSut(configuredKey: null, Environments.Production, context, new RecordingClientProxy());

        var result = await sut.StoragehookReceiveAsync(KnownEndpointId);

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
    }

    [TestMethod]
    public async Task Receive_allows_unconfigured_key_in_development()
    {
        var sut = CreateSut(configuredKey: null, Environments.Development, new DefaultHttpContext(), new RecordingClientProxy());

        var result = await sut.StoragehookReceiveAsync(KnownEndpointId);

        Assert.IsInstanceOfType(result, typeof(OkResult));
    }

    [TestMethod]
    public async Task Receive_with_valid_key_but_unknown_endpoint_returns_bad_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[StorageHookImplementation.WebhookKeyHeaderName] = ConfiguredKey;
        var sut = CreateSut(ConfiguredKey, Environments.Production, context, new RecordingClientProxy());

        var result = await sut.StoragehookReceiveAsync("unknown-endpoint");

        Assert.IsInstanceOfType(result, typeof(BadRequestResult));
    }

    private static StorageHookImplementation CreateSut(
        string? configuredKey, string environment, HttpContext httpContext, RecordingClientProxy proxy)
    {
        var configValues = new Dictionary<string, string?>();
        if (configuredKey is not null)
        {
            configValues["EventGrid:WebhookKey"] = configuredKey;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        return new StorageHookImplementation(
            new FakeHubContext(proxy),
            NullLogger<StorageHookImplementation>.Instance,
            new InMemoryMessageStore(),
            new FakePlatform(KnownEndpointId),
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeHostEnvironment(environment),
            configuration);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "NimBus.WebApp.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeHubContext : IHubContext<GridEventsHub>
    {
        public FakeHubContext(RecordingClientProxy proxy) => Clients = new FakeHubClients(proxy);

        public IHubClients Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly RecordingClientProxy _proxy;

        public FakeHubClients(RecordingClientProxy proxy) => _proxy = proxy;

        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public List<string> SentMethods { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            SentMethods.Add(method);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;

        public FakePlatform(params string[] endpointIds)
        {
            _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        }

        public IEnumerable<IEndpoint> Endpoints => _endpoints;
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

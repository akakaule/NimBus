#pragma warning disable CA1707, CA2007
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver;

namespace NimBus.Resolver.Tests;

[TestClass]
public class HttpEndpointStateChangeNotifierTests
{
    private static readonly Uri WebAppBase = new("http://webapp.local/");

    [TestMethod]
    public async Task Notify_PostsToStorageHookRoute_WithEscapedEndpointId()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = WebAppBase };
        var notifier = new HttpEndpointStateChangeNotifier(client, webhookKey: null);

        await notifier.NotifyEndpointStateChangedAsync("Crm Endpoint");

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        // AbsoluteUri preserves the wire-form escaping (ToString() decodes it for display).
        Assert.AreEqual(
            "http://webapp.local/api/storagehook/cosmos/Crm%20Endpoint",
            handler.RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task Notify_SendsWebhookKeyHeader_WhenConfigured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = WebAppBase };
        var notifier = new HttpEndpointStateChangeNotifier(client, webhookKey: "s3cret");

        await notifier.NotifyEndpointStateChangedAsync("BillingEndpoint");

        Assert.AreEqual("s3cret", handler.WebhookKey);
    }

    [TestMethod]
    public async Task Notify_OmitsWebhookKeyHeader_WhenNotConfigured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = WebAppBase };
        var notifier = new HttpEndpointStateChangeNotifier(client, webhookKey: "");

        await notifier.NotifyEndpointStateChangedAsync("BillingEndpoint");

        Assert.IsNull(handler.WebhookKey);
    }

    [TestMethod]
    public async Task Notify_SwallowsTransportFailures()
    {
        var handler = new ThrowingHandler();
        using var client = new HttpClient(handler) { BaseAddress = WebAppBase };
        var notifier = new HttpEndpointStateChangeNotifier(client, webhookKey: null);

        // Must not throw — realtime push is best-effort.
        await notifier.NotifyEndpointStateChangedAsync("BillingEndpoint");
    }

    [TestMethod]
    public async Task Notify_DoesNotPost_ForEmptyEndpointId()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = WebAppBase };
        var notifier = new HttpEndpointStateChangeNotifier(client, webhookKey: null);

        await notifier.NotifyEndpointStateChangedAsync("");

        Assert.IsFalse(handler.Sent, "No request should be sent for an empty endpoint id.");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CapturingHandler(HttpStatusCode status) => _status = status;

        public bool Sent { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? WebhookKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = true;
            Method = request.Method;
            RequestUri = request.RequestUri;
            WebhookKey = request.Headers.TryGetValues("X-Webhook-Key", out var values)
                ? string.Join(",", values)
                : null;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}

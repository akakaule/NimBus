using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NimBus.WebApp.Hubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.WebApp.Services;

// Webhook endpoints are anonymous because Azure Event Grid cannot authenticate via cookies/tokens.
// Security is enforced via a shared webhook key validated on every request — sent in the
// X-Webhook-Key header (preferred; configure as an Event Grid custom delivery header) or the
// legacy ?key= query parameter.
namespace NimBus.WebApp.ManagementApi
{
    [AllowAnonymous]
    public partial class StorageHookApiController : Controller { }
}

namespace NimBus.WebApp.Controllers
{
    public class StorageHookImplementation : IStorageHookApiController
    {
        /// <summary>
        /// Header carrying the webhook key. Preferred over the legacy ?key=
        /// query parameter because query strings end up in proxy and gateway
        /// access logs; Event Grid subscriptions can send it via custom
        /// delivery headers.
        /// </summary>
        public const string WebhookKeyHeaderName = "X-Webhook-Key";

        private readonly IHubContext<GridEventsHub> _hubContext;
        private readonly ILogger<StorageHookImplementation> _logger;
        private readonly INimBusMessageStore _cosmosClient;
        private readonly IPlatform _platform;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly string _webhookKey;

        public StorageHookImplementation(
            IHubContext<GridEventsHub> gridEventsHubContext,
            ILogger<StorageHookImplementation> logger,
            INimBusMessageStore cosmosClient,
            IPlatform platform,
            IHttpContextAccessor httpContextAccessor,
            IHostEnvironment hostEnvironment,
            IConfiguration configuration)
        {
            _hubContext = gridEventsHubContext;
            _logger = logger;
            _cosmosClient = cosmosClient;
            _platform = platform;
            _httpContextAccessor = httpContextAccessor;
            _hostEnvironment = hostEnvironment;
            _webhookKey = configuration.GetValue<string>("EventGrid:WebhookKey") ?? string.Empty;
        }

        // The storage-hook webhook is currently a Cosmos-only mechanism (Cosmos Change
        // Feed → Event Grid → here). The route name retains "Cosmos" for backwards
        // compatibility with the OpenAPI spec; the implementation delegates to a
        // provider-neutral handler that pushes the SignalR update. SQL deployments
        // receive the same SignalR pushes via the Resolver write-path notifier.
        [Obsolete("Renamed semantically to StoragehookReceiveAsync. The route name is kept for OpenAPI back-compat; future cleanup will rename both.")]
        public Task<IActionResult> StoragehookReceiveCosmosAsync(string endpointId)
            => StoragehookReceiveAsync(endpointId);

        public async Task<IActionResult> StoragehookReceiveAsync(string endpointId)
        {
            if (!ValidateWebhookKey())
                return new UnauthorizedResult();

            var endpointIdValid = EndpointVerificationService.EndpointExists(_platform, endpointId);
            if (endpointIdValid)
            {
                var state = await _cosmosClient.DownloadEndpointStateCount(endpointId);
                var endpointStatus = Mapper.EndpointStatusCountFromEndpointStateCount(state);
                await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.EndpointUpdate, endpointStatus);
                return new OkResult();
            }

            return new BadRequestResult();
        }

        /// <summary>
        /// Validates the webhook key from the X-Webhook-Key header (preferred) or,
        /// for existing Event Grid subscriptions, the legacy ?key= query parameter.
        /// Keys are compared in fixed time so the comparison does not leak key
        /// bytes through response timing. In Development, missing config falls
        /// back to a warning + allow so a local dashboard can run without Event
        /// Grid wiring; in any other environment a missing key fails closed to
        /// avoid leaving an anonymous endpoint open.
        /// </summary>
        private bool ValidateWebhookKey()
        {
            if (string.IsNullOrEmpty(_webhookKey))
            {
                if (_hostEnvironment.IsDevelopment())
                {
                    _logger.LogWarning("EventGrid:WebhookKey is not configured. Webhook endpoints are unprotected. Configure a webhook key before deploying.");
                    return true;
                }

                _logger.LogError("EventGrid:WebhookKey is not configured. Webhook request rejected — set EventGrid:WebhookKey to enable storage hooks in non-Development environments.");
                return false;
            }

            var request = _httpContextAccessor.HttpContext?.Request;
            var requestKey = request?.Headers[WebhookKeyHeaderName].ToString();
            if (string.IsNullOrEmpty(requestKey))
            {
                requestKey = request?.Query["key"].ToString();
            }

            if (string.IsNullOrEmpty(requestKey) || !FixedTimeEquals(requestKey, _webhookKey))
            {
                _logger.LogWarning("Webhook request rejected: invalid or missing webhook key");
                return false;
            }

            return true;
        }

        private static bool FixedTimeEquals(string left, string right)
            => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(left),
                System.Text.Encoding.UTF8.GetBytes(right));
    }
}

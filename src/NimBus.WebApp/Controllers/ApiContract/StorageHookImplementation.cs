using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NimBus.WebApp.Hubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.WebApp.Services;

// Webhook endpoints are anonymous because Azure Event Grid cannot authenticate via cookies/tokens.
// Security is enforced via a shared webhook key validated on every request.
namespace NimBus.WebApp.ManagementApi
{
    [AllowAnonymous]
    public partial class StorageHookApiController : Controller { }
}

namespace NimBus.WebApp.Controllers
{
    public class StorageHookImplementation : IStorageHookApiController
    {
        private readonly IHubContext<GridEventsHub> _hubContext;
        private readonly ILogger<StorageHookImplementation> _logger;
        private readonly ICosmosDbClient _cosmosClient;
        private readonly IPlatform _platform;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly string _webhookKey;

        public StorageHookImplementation(
            IHubContext<GridEventsHub> gridEventsHubContext,
            ILogger<StorageHookImplementation> logger,
            ICosmosDbClient cosmosClient,
            IPlatform platform,
            IHttpContextAccessor httpContextAccessor,
            IConfiguration configuration)
        {
            _hubContext = gridEventsHubContext;
            _logger = logger;
            _cosmosClient = cosmosClient;
            _platform = platform;
            _httpContextAccessor = httpContextAccessor;
            _webhookKey = configuration.GetValue<string>("EventGrid:WebhookKey") ?? string.Empty;
        }

        public async Task<IActionResult> StoragehookReceiveCosmosAsync(string endpointId)
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

        public async Task<IActionResult> PostApiStoragehookHeartbeatEndpointIdAsync(string endpointId)
        {
            if (!ValidateWebhookKey())
                return new UnauthorizedResult();

            var endpointIdValid = EndpointVerificationService.EndpointExists(_platform, endpointId);
            if (endpointIdValid)
            {
                var metadata = await _cosmosClient.GetEndpointMetadata(endpointId);
                var metadataShort = Mapper.MetadataShortFromMetadata(metadata);
                await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.HeartbeatUpdate, metadataShort);
                return new OkResult();
            }

            return new BadRequestResult();
        }

        /// <summary>
        /// Validates the webhook key from the request query string against the configured key.
        /// When no key is configured, requests are allowed with a warning to preserve backward compatibility.
        /// </summary>
        private bool ValidateWebhookKey()
        {
            if (string.IsNullOrEmpty(_webhookKey))
            {
                _logger.LogWarning("EventGrid:WebhookKey is not configured. Webhook endpoints are unprotected. Configure a webhook key for production security.");
                return true;
            }

            var requestKey = _httpContextAccessor.HttpContext?.Request.Query["key"].ToString();
            if (string.IsNullOrEmpty(requestKey) || !string.Equals(requestKey, _webhookKey, StringComparison.Ordinal))
            {
                _logger.LogWarning("Webhook request rejected: invalid or missing webhook key");
                return false;
            }

            return true;
        }
    }
}

using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NimBus.WebApp.Hubs;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.WebApp.Services;

// Webhook endpoints need to be anonymous for Azure Event Grid callbacks.
// TODO: Implement Azure Event Grid webhook signature validation for security.
// See: https://learn.microsoft.com/en-us/azure/event-grid/webhook-event-delivery#endpoint-validation-with-event-grid-events
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

        private readonly ILogger<StorageHookImplementation> logger;
        private readonly ICosmosDbClient _cosmosClient;
        private readonly IPlatform platform;

        public StorageHookImplementation(IHubContext<GridEventsHub> gridEventsHubContext, ILogger<StorageHookImplementation> logger, ICosmosDbClient cosmosClient, IPlatform platform)
        {
            this._hubContext = gridEventsHubContext;
            this.logger = logger;
            this._cosmosClient = cosmosClient;
            this.platform = platform;
        }

        public async Task<IActionResult> StoragehookReceiveCosmosAsync(string endpointId)
        {
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
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
            var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
            if (endpointIdValid)
            {
                var metadata = await _cosmosClient.GetEndpointMetadata(endpointId);
                var metadataShort = Mapper.MetadataShortFromMetadata(metadata);
                await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.HeartbeatUpdate, metadataShort);
                return new OkResult();
            }

            return new BadRequestResult();
        }
    }
}

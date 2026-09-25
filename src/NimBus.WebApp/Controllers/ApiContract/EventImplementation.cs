using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Messages.PII;
using NimBus.Manager;
using NimBus.MessageStore.Abstractions;
using NimBus.SDK;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.ApplicationInsights;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation : IEventApiController
{
    // Payload-carrying request message types — the ones that actually carry
    // the original event JSON to be re-delivered on resubmit. Handoff control
    // requests (HandoffCompleted/FailedRequest) and SkipRequest carry no
    // payload; terminal *Response messages may carry a stale/empty payload
    // (notably a failed hand-off's ErrorResponse). Mirrored by the frontend's
    // PAYLOAD_REQUEST_TYPES in message-listing.tsx.
    private static readonly Core.Messages.MessageType[] PayloadCarryingRequestTypes =
    {
        Core.Messages.MessageType.EventRequest,
        Core.Messages.MessageType.ResubmissionRequest,
        Core.Messages.MessageType.RetryRequest,
        Core.Messages.MessageType.ContinuationRequest,
        Core.Messages.MessageType.ProcessDeferredRequest,
    };

    private readonly IPlatform platform;
    private readonly ILogger<EventImplementation> logger;
    private readonly IMessageTrackingStore messageStore;
    private readonly IManagerClient managerClient;
    private readonly IHandoffClientFactory handoffClients;
    private readonly IApplicationInsightsService applicationInsightsService;
    private readonly IEndpointAuthorizationService authorizationService;
    private readonly IAdminService adminService;
    private readonly ServiceBusClient serviceBusClient;
    private readonly ServiceBusAdministrationClient? serviceBusAdministrationClient;
    private readonly IAuditLogService auditLogService;
    private readonly IHandoffSettlementService handoffSettlement;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly PayloadRedaction payloadRedaction;
    private readonly IEventJsonMasker masker;

    public EventImplementation(
        IApplicationInsightsService applicationInsightsService,
        IPlatform platform,
        IManagerClient managerClient,
        IHandoffClientFactory handoffClientFactory,
        ILogger<EventImplementation> logger,
        IMessageTrackingStore messageStore,
        IEndpointAuthorizationService authorizationService,
        IAdminService adminService,
        ServiceBusClient serviceBusClient,
        IAuditLogService auditLogService,
        IHandoffSettlementService handoffSettlement,
        IHttpContextAccessor httpContextAccessor,
        PayloadRedaction payloadRedaction,
        IEventJsonMasker masker,
        ServiceBusAdministrationClient? serviceBusAdministrationClient = null)
    {
        this.payloadRedaction = payloadRedaction;
        this.masker = masker ?? NullEventJsonMasker.Instance;
        this.platform = platform;
        this.logger = logger;
        this.messageStore = messageStore;
        this.managerClient = managerClient;
        this.handoffClients = handoffClientFactory;
        this.applicationInsightsService = applicationInsightsService;
        this.authorizationService = authorizationService;
        this.adminService = adminService;
        this.serviceBusClient = serviceBusClient;
        this.serviceBusAdministrationClient = serviceBusAdministrationClient;
        this.auditLogService = auditLogService;
        this.handoffSettlement = handoffSettlement;
        this.httpContextAccessor = httpContextAccessor;
    }

    // The platform definition's casing is canonical — store writes and
    // exact-match lookups (Cosmos partition keys, audit grouping) must all
    // use it regardless of the casing the request arrived with.
    private string CanonicalEndpointId(string endpointId) =>
        platform.Endpoints.FirstOrDefault(e => e.Id.Equals(endpointId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? endpointId;
}

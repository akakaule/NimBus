using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Http;
using NimBus.Core;
using NimBus.Core.Endpoints;
using Microsoft.Extensions.Configuration;
using NimBus.Management.ServiceBus;
using System.Security.Claims;
using System.Text;
using System.Reflection;
using System.IO;
using NimBus.MessageStore.States;
using EndpointSubscription = NimBus.WebApp.ManagementApi.EndpointSubscription;
using System.Collections.Concurrent;
using TechnicalContact = NimBus.MessageStore.States.TechnicalContact;
using NimBus.WebApp.Services;
using System.Threading;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Controllers;

public class EndpointImplementation : IEndpointApiController
{
    private readonly IPlatform platform;
    private readonly IConfiguration configuration;
    private readonly INimBusMessageStore cosmosClient;
    private readonly IServiceBusManagement serviceBusManagement;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly HttpContext _context;
    private readonly ILogger<EndpointImplementation> _logger;
    private readonly IAuditLogService _auditLogService;
    private readonly IStoreResultCache _storeResultCache;
    private const int InitialEvents = 40;
    private const int PagingEvents = 40;
    private const int SqlInvalidObjectNameErrorNumber = 208;

    // Status counts back the Monitor wall, which polls every few seconds per
    // client; a 5s TTL collapses that fan-in to at most one store round-trip
    // per endpoint per window without visibly staling the UI.
    private static readonly TimeSpan StatusCountTtl = TimeSpan.FromSeconds(5);

    public EndpointImplementation(
        IHttpContextAccessor contextAccessor,
        IPlatform platform,
        IConfiguration configuration,
        INimBusMessageStore cosmosClient,
        IServiceBusManagement serviceBusManagement,
        IEndpointAuthorizationService authorizationService,
        ILogger<EndpointImplementation> logger,
        IAuditLogService auditLogService,
        IStoreResultCache storeResultCache)
    {
        this.platform = platform;
        this.configuration = configuration;
        this.cosmosClient = cosmosClient;
        this.serviceBusManagement = serviceBusManagement;
        this._authorizationService = authorizationService;
        _context = contextAccessor.HttpContext;
        _logger = logger;
        _auditLogService = auditLogService;
        _storeResultCache = storeResultCache;
    }

    public async Task<ActionResult<IEnumerable<string>>> EndpointIdsAllAsync()
    {
        var endpointIds = platform.Endpoints
            .Where(endpoint => _authorizationService.IsManagerOfEndpoint(endpoint.Id) && ShowEndpoint(endpoint.Id))
            .Select(e => e.Id);

        return new OkObjectResult(endpointIds);
    }

    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> EndpointstatusAllAsync()
    {
        var endpointIds = platform.Endpoints
            .Where(endpoint => _authorizationService.IsManagerOfEndpoint(endpoint.Id) && ShowEndpoint(endpoint.Id))
            .Select(e => e.Id)
            .ToList();

        // Each count is an independent storage aggregate query (~100-500ms against
        // Cosmos); run them concurrently (bounded) rather than serially so a
        // 40-endpoint list doesn't block for many seconds. Results go into a
        // pre-sized array by index to preserve endpoint order.
        var endpointStateCounts = new EndpointStateCount[endpointIds.Count];

        try
        {
            var countOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
            await Parallel.ForEachAsync(Enumerable.Range(0, endpointIds.Count), countOptions, async (i, _) =>
            {
                endpointStateCounts[i] = await cosmosClient.DownloadEndpointStateCount(endpointIds[i]);
            });
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult("Endpoint container not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult("Endpoint container not found in database");
        }

        return new OkObjectResult(endpointStateCounts.Select(Mapper.EndpointStatusCountFromEndpointStateCount));
    }

    public async Task<ActionResult<IEnumerable<Event>>> EndpointstatusAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        try
        {
            var endpointState = await cosmosClient.DownloadEndpointStatePaging(endpointName, InitialEvents, "");
            if (endpointState == null)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            var eventIds = endpointState.FailedEvents
                .Concat(endpointState.DeferredEvents)
                .Concat(endpointState.PendingEvents)
                .Distinct()
                .ToList();

            var events = await cosmosClient.GetEventsByIds(endpointName, eventIds);
            var res = events.ToDictionary(e => e.EventId, Mapper.EventFromMessageStoreEvent);

            return new OkObjectResult(res);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
    }

    public async Task<ActionResult<EndpointStatus>> GetApiEndpointstatusStatusEndpointNameAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            var endpoint = await cosmosClient.DownloadEndpointStatePaging(endpointName, InitialEvents, "");
            return new OkObjectResult(Mapper.EndpointStatusFromEndpointState(endpoint));
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<SessionStatus>> GetEndpointSessionStatusAsync(string endpointId,
        string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (endpointIdValid)
        {
            try
            {
                var sessionState = await cosmosClient.DownloadEndpointSessionStateCount(endpointId, sessionId);
                return new OkObjectResult(Mapper.SessionStatusFromSessionState(sessionState));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<EndpointStatus>> PostApiEndpointStatusEndpointNameTokenAsync(
        ContinuationToken body, string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            var endpoint = await cosmosClient.DownloadEndpointStatePaging(endpointName, PagingEvents, body.Token);
            return new OkObjectResult(Mapper.EndpointStatusFromEndpointState(endpoint));
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    private bool ShowEndpoint(string endpointId)
    {
        if (!configuration.GetValue<string>("Environment").Equals("dev", StringComparison.OrdinalIgnoreCase) &&
            !configuration.GetValue<string>("Environment").Equals("sbdev", StringComparison.OrdinalIgnoreCase))
        {
            var filterList = new List<string> { "Alice", "Bob", "Charlie" };

            return !filterList.Contains(endpointId, StringComparer.OrdinalIgnoreCase);
        }

        return true;
    }


    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> GetEndpointStatusCountAllAsync()
    {
        var endpointIds = platform.Endpoints
            .Where(endpoint => _authorizationService.IsManagerOfEndpoint(endpoint.Id) && ShowEndpoint(endpoint.Id))
            .Select(e => e.Id);

        // Each status download is an independent Cosmos round-trip; probe them
        // concurrently (bounded) instead of serially so the all-endpoints view
        // isn't O(endpoints) × latency. Mirrors PostApiEndpointStatusCountAsync.
        var result = new ConcurrentBag<EndpointStatusCount>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
        await Parallel.ForEachAsync(endpointIds, options, async (endpointId, _) =>
        {
            result.Add(await DownloadStatusOrStubAsync(endpointId));
        });

        return new OkObjectResult(result.ToList());
    }

    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> PostApiEndpointStatusCountAsync(IEnumerable<string> body)
    {
        var result = new ConcurrentBag<EndpointStatusCount>();
        var endpointIds = (body as string[] ?? body.ToArray())
            .Where(id => EndpointVerificationService.EndpointExists(platform, id)
                      && _authorizationService.IsManagerOfEndpoint(id));

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };

        await Parallel.ForEachAsync(endpointIds, options, async (endpointId, _) =>
        {
            result.Add(await DownloadStatusOrStubAsync(endpointId));
        });

        return new OkObjectResult(result.ToList());
    }

    private async Task<EndpointStatusCount> DownloadStatusOrStubAsync(string endpointId)
    {
        try
        {
            // Cached below the per-user IsManagerOfEndpoint filtering (which
            // happens at the call sites above): the cached value is the
            // endpoint-scoped raw count, identical for every authorized user.
            // Failures propagate uncached, so the stub branches below stay live.
            var endpointStateCount = await _storeResultCache.GetOrCreateAsync(
                $"endpoint-state-count:{endpointId}",
                StatusCountTtl,
                () => cosmosClient.DownloadEndpointStateCount(endpointId));
            return Mapper.EndpointStatusCountFromEndpointStateCount(endpointStateCount);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Storage container missing for endpoint {EndpointId} (Cosmos 404)", endpointId);
            return StorageUnavailableStub(endpointId);
        }
        catch (EndpointNotFoundException ex)
        {
            _logger.LogWarning(ex, "Storage container missing for endpoint {EndpointId} (EndpointNotFoundException)", endpointId);
            return StorageUnavailableStub(endpointId);
        }
        catch (SqlException ex) when (ex.Number == SqlInvalidObjectNameErrorNumber)
        {
            _logger.LogWarning(ex, "Storage table missing for endpoint {EndpointId} (SQL 208)", endpointId);
            return StorageUnavailableStub(endpointId);
        }
    }

    private static EndpointStatusCount StorageUnavailableStub(string endpointId)
        => new EndpointStatusCount
        {
            EndpointId = endpointId,
            StorageStatus = "unavailable",
        };

    public async Task<ActionResult<IEnumerable<Event>>> GetEndpointStatusIdAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        try
        {
            var endpointState = await cosmosClient.DownloadEndpointStatePaging(endpointName, InitialEvents, "");
            if (endpointState == null)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            var eventIds = endpointState.FailedEvents
                .Concat(endpointState.DeferredEvents)
                .Concat(endpointState.PendingEvents)
                .Distinct()
                .ToList();

            var events = await cosmosClient.GetEventsByIds(endpointName, eventIds);
            var res = events.ToDictionary(e => e.EventId, Mapper.EventFromMessageStoreEvent);

            return new OkObjectResult(res);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
    }

    public async Task<ActionResult<IEnumerable<string>>> GetEndpointsAllAsync()
    {
        var endpointIds = platform.Endpoints
            .Where(endpoint => _authorizationService.IsManagerOfEndpoint(endpoint.Id) && ShowEndpoint(endpoint.Id))
            .Select(e => e.Id);

        return new OkObjectResult(endpointIds);
    }

    public async Task<ActionResult<EndpointStatusCount>> GetEndpointStatusCountIdAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            if (!_authorizationService.IsManagerOfEndpoint(endpointName))
            {
                await _auditLogService.LogAuditAsync(MessageAuditType.GetEndpointDetails, _context,
                    accessDenied: true, endpointId: endpointName);
                return new ForbidResult();
            }

            await _auditLogService.LogAuditAsync(MessageAuditType.GetEndpointDetails, _context,
                endpointId: endpointName);

            try
            {
                var state = await cosmosClient.DownloadEndpointStateCount(endpointName);
                var result = Mapper.EndpointStatusCountFromEndpointStateCount(state);

                return new OkObjectResult(result);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointName}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointName}' not found in database");
            }
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<EndpointStatus>> PostEndpointStatusIdTokenAsync(ContinuationToken body,
        string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            var endpoint = await cosmosClient.DownloadEndpointStatePaging(endpointName, PagingEvents, body.Token);
            return new OkObjectResult(Mapper.EndpointStatusFromEndpointState(endpoint));
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<SessionStatus>> GetEndpointSessionIdAsync(string endpointId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (endpointIdValid)
        {
            try
            {
                var sessionState = await cosmosClient.DownloadEndpointSessionStateCount(endpointId, sessionId);
                return new OkObjectResult(Mapper.SessionStatusFromSessionState(sessionState));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
            }
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<IEnumerable<SessionStatus>>> PostEndpointSessionsBatchAsync(IEnumerable<string> body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        try
        {
            var sessionStates = await cosmosClient.DownloadEndpointSessionStateCountBatch(endpointId, body);
            var result = sessionStates.Select(Mapper.SessionStatusFromSessionState).ToList();
            return new OkObjectResult(result);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
    }

    public async Task<IActionResult> PostEndpointPurgeAsync(string endpointName)
    {
        var isManagementUser = IsUserInSecurityGroup("EIP_Management");

        // Validate environment
        var env = configuration.GetValue<string>("Environment");
        if (!isManagementUser && (env.Equals("prod", StringComparison.OrdinalIgnoreCase) ||
                                  env.Equals("stag", StringComparison.OrdinalIgnoreCase)))
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointName,
                data: $"Environment={env}");
            return new NotFoundObjectResult("Endpoint cannot be purged in Production and Staging environments");
        }

        // Validate endpoint
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Purge endpoint
        var isPurged = await cosmosClient.PurgeMessages(endpointName);

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        await endpointManagement.ClearEndpoint(endpointName);

        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointName);

        if (isPurged)
            return new OkObjectResult($"{endpointName} is purged");

        return new NotFoundObjectResult($"{endpointName} couldn't be found");
    }

    // Restrict the match to the "groups" claim type so non-group claims cannot
    // elevate privileges. Mirrors EndpointAuthorizationService.IsUserInGroup.
    private bool IsUserInSecurityGroup(string securityGrp)
    {
        var userClaims = _context.User.Identities.First().Claims;
        return userClaims.Any(c => c.Type == "groups" && c.Value == securityGrp);
    }

    public async Task<IActionResult> PostEndpointSubscribeAsync(EndpointSubscription body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (endpointIdValid)
        {
            var subscriptionStatus = await cosmosClient.SubscribeToEndpointNotification(endpointId, body.Mail,
            body.Type, GetCurrentUsersMail(), body.Url, body.EventTypes, body.Payload, body.Frequency);
            return new OkObjectResult(subscriptionStatus);
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<IActionResult> DeleteEndpointSubscribeAsync(SubscriptionAuthor body, string endpointId)
    {
        // Validate endpoint
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        var success = await cosmosClient.DeleteSubscription(body.Id);

        if (success)
            return new OkResult();

        return new BadRequestResult();
    }

    async Task<ActionResult<IEnumerable<EndpointSubscription>>> IEndpointApiController.GetEndpointSubscribeAsync(
        string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (endpointIdValid)
        {
            var subscriptions = await cosmosClient.GetSubscriptionsOnEndpoint(endpointId);
            return new OkObjectResult(Mapper.SubscriptionsFromEndpointsubscriptions(subscriptions));
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    private string GetCurrentUsersMail()
    {
        var name = _context.User.Identities.FirstOrDefault()?.Name;

        if (string.IsNullOrEmpty(name))
            name = _context.User.FindFirst(ClaimTypes.Name).Value;

        return name;
    }

    public async Task<ActionResult<string>> GetEndpointRoleAssignmentScriptAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        var builder = new StringBuilder();

        var subscriptionId = configuration.GetSection("ServiceBusManagement:SubscriptionId").Value;
        var environment = configuration.GetSection("Environment").Value;
        var resourceGroupName = configuration.GetSection("ServiceBusManagement:ResourceGroupName").Value;
        var serviceBusNamespace = configuration.GetSection("ServiceBusNamespace").Value;
        var endpoint = platform.Endpoints.FirstOrDefault(ep => ep.Id.Equals(endpointId, StringComparison.OrdinalIgnoreCase));

        if (endpoint == null)
        {
            return new NotFoundObjectResult($"Endpoint '{endpointId}' not found");
        }

        var principals = endpoint.RoleAssignments
            .Where(x => x.Environment.Equals(environment, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.PrincipalId).ToList();
        if (principals.Count > 0)
        {
            await using Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EET.EIP.WebApp.Resources.EndpointRoleAssignment.ps1");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                builder.AppendLine($"$subscription =\"{subscriptionId}\" \n");
                builder.AppendLine($"$resourceGroupName = \"{resourceGroupName}\" \n");
                builder.Append(await reader.ReadToEndAsync());
                builder.AppendLine();
                foreach (var principal in principals)
                {
                    builder.AppendLine(
                        $"Assign-ServiceBusSubscription -assigneeId \"{principal}\" -serviceBusNamespace \"{serviceBusNamespace}\" -topic \"{endpoint.Id}\" -subscription \"{endpoint.Id}\" \n");
                }
            }
        }

        return new OkObjectResult(builder.ToString());
    }

    public async Task<IActionResult> PostEndpointSubscriptionstatusAsync(string body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Resolve the audit-type up front; both branches need it for the
        // access-denied path and the success path.
        MessageAuditType? auditType = body switch
        {
            "enable" => MessageAuditType.EnableEndpoint,
            "disable" => MessageAuditType.DisableEndpoint,
            _ => null,
        };

        if (auditType.HasValue && !_authorizationService.IsManagerOfEndpoint(endpointId))
        {
            await _auditLogService.LogAuditAsync(auditType.Value, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        switch (body)
        {
            case "enable":
                {
                    await endpointManagement.EnableEndpoint(endpointId);
                    var metadata = await cosmosClient.GetEndpointMetadata(endpointId);
                    metadata.SubscriptionStatus = true;
                    await cosmosClient.SetEndpointMetadata(metadata);
                    await _auditLogService.LogAuditAsync(MessageAuditType.EnableEndpoint, _context,
                        endpointId: endpointId);
                    if (await endpointManagement.IsEndpointActive(endpointId))
                        return new OkObjectResult($"{endpointId} is active");
                    break;
                }
            case "disable":
                {
                    await endpointManagement.DisableEndpoint(endpointId);
                    var metadata = await cosmosClient.GetEndpointMetadata(endpointId);
                    metadata.SubscriptionStatus = false;
                    await cosmosClient.SetEndpointMetadata(metadata);
                    await _auditLogService.LogAuditAsync(MessageAuditType.DisableEndpoint, _context,
                        endpointId: endpointId);
                    if (!await endpointManagement.IsEndpointActive(endpointId))
                        return new OkObjectResult($"{endpointId} is disable");
                    break;
                }
        }

        return new NotFoundObjectResult($"{endpointId} status not set");
    }

    public async Task<ActionResult<string>> GetEndpointSubscriptionstatusAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        var subscriptionState = await endpointManagement.GetEndpointSubscriptionState(endpointId);
        if (subscriptionState == SubscriptionState.Active)
        {
            return new OkObjectResult($"active");
        }

        if (subscriptionState == SubscriptionState.NotFound)
        {
            return new OkObjectResult("not-found");
        }

        return new OkObjectResult("disabled");
    }

    public async Task<IActionResult> PostEndpointSendstatusAsync(string body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        MessageAuditType? auditType = body switch
        {
            "enable" => MessageAuditType.EnableEndpointSend,
            "disable" => MessageAuditType.DisableEndpointSend,
            _ => null,
        };

        if (auditType.HasValue && !_authorizationService.IsManagerOfEndpoint(endpointId))
        {
            await _auditLogService.LogAuditAsync(auditType.Value, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        switch (body)
        {
            case "enable":
                {
                    await endpointManagement.EnableEndpointSend(endpointId);
                    await _auditLogService.LogAuditAsync(MessageAuditType.EnableEndpointSend, _context,
                        endpointId: endpointId);
                    if (await endpointManagement.GetEndpointSendState(endpointId) == TopicSendState.Enabled)
                        return new OkObjectResult($"{endpointId} send is active");
                    break;
                }
            case "disable":
                {
                    await endpointManagement.DisableEndpointSend(endpointId);
                    await _auditLogService.LogAuditAsync(MessageAuditType.DisableEndpointSend, _context,
                        endpointId: endpointId);
                    if (await endpointManagement.GetEndpointSendState(endpointId) == TopicSendState.SendDisabled)
                        return new OkObjectResult($"{endpointId} send is disabled");
                    break;
                }
        }

        return new NotFoundObjectResult($"{endpointId} send status not set");
    }

    public async Task<ActionResult<string>> GetEndpointSendstatusAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        var sendState = await endpointManagement.GetEndpointSendState(endpointId);
        if (sendState == TopicSendState.Enabled)
        {
            return new OkObjectResult($"active");
        }

        if (sendState == TopicSendState.NotFound)
        {
            return new OkObjectResult("not-found");
        }

        return new OkObjectResult("disabled");
    }

    public async Task<ActionResult<Metadata>> GetMetadataEndpointAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        var metadata = await cosmosClient.GetEndpointMetadata(endpointId);
        if (metadata == null)
            return new NotFoundObjectResult($"Metadata for {endpointId} not found");
        if (metadata.SubscriptionStatus == null)
        {
            await SetSubscriptionStatusMetadata(metadata);
        }

        return new OkObjectResult(Mapper.MetadataFromEndpointMetadata(metadata));
    }




    public async Task<ActionResult<IEnumerable<MetadataShort>>> PostApiMetadatashortAsync(IEnumerable<string> body)
    {
        var endpointIds = body.ToList();
        var metadataList = await cosmosClient.GetMetadatas(endpointIds) ?? endpointIds.Select(x => new EndpointMetadata { EndpointId = x }).ToList();

        foreach (var id in endpointIds)
        {
            if (!EndpointVerificationService.EndpointExists(platform, id))
            {
                return new NotFoundObjectResult(String.Format("Endpoint with id {0} not found",id));
            }
        } 

        foreach (var s in endpointIds.Where(s => !metadataList.Exists(m => m.EndpointId.Equals(s, StringComparison.OrdinalIgnoreCase))))
        {
            metadataList.Add(new EndpointMetadata { EndpointId = s });
        }

        // Each probe is an independent Service Bus admin REST call (~100-500ms);
        // run them concurrently (bounded) rather than serially so a 40-endpoint
        // list doesn't block for many seconds. Each call mutates its own metadata
        // object, so there's no shared state to guard.
        var pendingStatus = metadataList.Where(m => m.SubscriptionStatus == null);
        var statusOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
        await Parallel.ForEachAsync(pendingStatus, statusOptions, async (endpointMetadata, _) =>
        {
            await SetSubscriptionStatusMetadata(endpointMetadata);
        });

        return new OkObjectResult(Mapper.MetadataShortFromList(metadataList));
    }


    public async Task<IActionResult> PostMetadataEndpointAsync(Metadata body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        var technicalContacts = body.TechnicalContacts
            .Select(technicalContact => new TechnicalContact()
            { Name = technicalContact.Name, Email = technicalContact.Email }).ToList();


        var metadataStatus = await cosmosClient.SetEndpointMetadata(
            new EndpointMetadata
            {
                EndpointOwner = body.EndpointOwner,
                EndpointId = body.Id,
                EndpointOwnerTeam = body.EndpointOwnerTeam,
                EndpointOwnerEmail = body.EndpointOwnerEmail,
                TechnicalContacts = technicalContacts,
            });
        return new OkObjectResult(metadataStatus);
    }

    private async Task SetSubscriptionStatusMetadata(EndpointMetadata metadata)
    {
        // Querying the Service Bus admin REST endpoint can fail in two known
        // scenarios: (1) running against the emulator, whose admin REST port
        // (5300) isn't reachable through the standard connection string the
        // SDK uses, and (2) transient real-Azure outages during page loads.
        // Either way, a single subscription-status probe failing should not
        // 500 the entire endpoints list — leave SubscriptionStatus null
        // ("unknown") and let the page still render.
        SubscriptionState? subscriptionState = null;
        try
        {
            var endpointManagement = new EndpointManagement(serviceBusManagement);
            subscriptionState = await endpointManagement.GetEndpointSubscriptionState(metadata.EndpointId);
        }
        catch (Exception)
        {
            // Swallow — SubscriptionStatus stays null, surfaced in UI as "unknown".
        }

        metadata.SubscriptionStatus = subscriptionState switch
        {
            SubscriptionState.Active => true,
            SubscriptionState.Disabled => false,
            _ => null,
        };

        // Only spend RU persisting a *known* status. A failed/unknown probe leaves
        // SubscriptionStatus null for this render without a redundant "unknown"
        // upsert (the previous code wrote on every probe, including failures).
        if (metadata.SubscriptionStatus != null)
        {
            await cosmosClient.SetEndpointMetadata(metadata);
        }
    }
}

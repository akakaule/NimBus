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

namespace NimBus.WebApp.Controllers;

public class EndpointImplementation : IEndpointApiController
{
    private readonly IPlatform platform;
    private readonly IConfiguration configuration;
    private readonly INimBusMessageStore cosmosClient;
    private readonly IServiceBusManagement serviceBusManagement;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly HttpContext _context;
    private const int InitialEvents = 40;
    private const int PagingEvents = 40;

    public EndpointImplementation(
        IHttpContextAccessor contextAccessor,
        IPlatform platform,
        IConfiguration configuration,
        INimBusMessageStore cosmosClient,
        IServiceBusManagement serviceBusManagement,
        IEndpointAuthorizationService authorizationService)
    {
        this.platform = platform;
        this.configuration = configuration;
        this.cosmosClient = cosmosClient;
        this.serviceBusManagement = serviceBusManagement;
        this._authorizationService = authorizationService;
        _context = contextAccessor.HttpContext;
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
            .Select(e => e.Id);

        var endpointStateCounts = new List<EndpointStateCount>();

        try
        {
            foreach (var endpointId in endpointIds)
            {
                endpointStateCounts.Add(await cosmosClient.DownloadEndpointStateCount(endpointId));
            }
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

        var endpointStateCounts = new List<EndpointStateCount>();

        try
        {
            foreach (var endpointId in endpointIds)
            {
                endpointStateCounts.Add(await cosmosClient.DownloadEndpointStateCount(endpointId));
            }
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

    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> PostApiEndpointStatusCountAsync(IEnumerable<string> body)
    {
        var result = new ConcurrentBag<EndpointStatusCount>();
        var endpointIds = body as string[] ?? body.ToArray();

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
        var returnObjectIfCancelled = new NotFoundObjectResult("Endpoint not found");

        using (var cts = new CancellationTokenSource())
        {
            var par = Parallel.ForEachAsync(endpointIds, options, async (endpointId, token) =>
            {
                token.ThrowIfCancellationRequested(); // Check for cancellation before proceeding

                var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
                if (endpointIdValid)
                {
                    try
                    {
                        var endpointStateCount = await cosmosClient.DownloadEndpointStateCount(endpointId);
                        result.Add(Mapper.EndpointStatusCountFromEndpointStateCount(endpointStateCount));
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        cts.Cancel();
                        returnObjectIfCancelled = new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
                    }
                    catch (EndpointNotFoundException)
                    {
                        cts.Cancel();
                        returnObjectIfCancelled = new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
                    }
                } else
                {
                    cts.Cancel();
                    returnObjectIfCancelled = new NotFoundObjectResult("Endpoint not found");
                }
            });

            try
            {
                await par;
                return new OkObjectResult(result.ToList());
            }
            catch (OperationCanceledException)
            {
                return returnObjectIfCancelled;
            }
        }

    }

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
            return new NotFoundObjectResult("Endpoint cannot be purged in Production and Staging environments");

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

        if (isPurged)
            return new OkObjectResult($"{endpointName} is purged");

        return new NotFoundObjectResult($"{endpointName} couldn't be found");
    }

    private bool IsUserInSecurityGroup(string securityGrp)
    {
        var userClaims = _context.User.Identities.First().Claims;
        return userClaims.Any(c => c.Value == securityGrp);
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

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        switch (body)
        {
            case "enable":
                {
                    await endpointManagement.EnableEndpoint(endpointId);
                    var metadata = await cosmosClient.GetEndpointMetadata(endpointId);
                    metadata.SubscriptionStatus = true;
                    await cosmosClient.SetEndpointMetadata(metadata);
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

    public async Task<IActionResult> EndpointEnableHeartbeatAsync(bool? body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!body.HasValue)
            return new BadRequestResult();

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        await endpointManagement.EnableHeartbeatOnEndpoint(endpointId, body.Value);
        await cosmosClient.EnableHeartbeatOnEndpoint(endpointId, body.Value);

        return new OkObjectResult(body);
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

        foreach (var endpointMetadata in metadataList.Where(endpointMetadata => endpointMetadata.SubscriptionStatus == null))
        {
            await SetSubscriptionStatusMetadata(endpointMetadata);
        }

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

        await cosmosClient.SetEndpointMetadata(metadata);
    }
}

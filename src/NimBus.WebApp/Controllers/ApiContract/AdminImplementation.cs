using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.ServiceBus.AsyncApi;
using NimBus.ServiceBus.Provisioning;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using AsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;

namespace NimBus.WebApp.Controllers.ApiContract;

public class AdminImplementation : IAdminApiController
{
    private readonly IAdminService _adminService;
    private readonly ISubscriptionAdminService _subscriptionAdminService;
    private readonly IPlatform _platform;
    private readonly IConfiguration _configuration;
    private readonly HttpContext _context;
    private readonly IAuditLogService _auditLogService;
    private readonly IEndpointAuthorizationService _authorizationService;

    public AdminImplementation(
        IHttpContextAccessor contextAccessor,
        IAdminService adminService,
        ISubscriptionAdminService subscriptionAdminService,
        IPlatform platform,
        IConfiguration configuration,
        IAuditLogService auditLogService,
        IEndpointAuthorizationService authorizationService)
    {
        _adminService = adminService;
        _subscriptionAdminService = subscriptionAdminService;
        _platform = platform;
        _configuration = configuration;
        _context = contextAccessor.HttpContext;
        _auditLogService = auditLogService;
        _authorizationService = authorizationService;
    }

    public async Task<ActionResult<PlatformConfig>> GetAdminPlatformConfigAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var result = await _adminService.GetPlatformConfigAsync(_platform);
        return new OkObjectResult(result);
    }

    // Full-platform AsyncAPI 3.0 export. Admin-only (EIP_Management), matching platform-config access.
    // Authorization is checked before any serialization work. Missing/empty format defaults to YAML;
    // only 'yaml' and 'json' are accepted — anything else is a 400 (never a silent default).
    public async Task<IActionResult> GetAdminAsyncapiAsync(string format)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        AsyncApiFormat exportFormat;
        string fileName;
        string contentType;

        if (string.IsNullOrWhiteSpace(format) ||
            string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase))
        {
            exportFormat = AsyncApiFormat.Yaml;
            fileName = "nimbus-asyncapi.yaml";
            contentType = "application/x-yaml";
        }
        else if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            exportFormat = AsyncApiFormat.Json;
            fileName = "nimbus-asyncapi.json";
            contentType = "application/json";
        }
        else
        {
            return new BadRequestObjectResult($"Unsupported format '{format}'. Use 'yaml' or 'json'.");
        }

        var content = AsyncApiExporter.Serialize(_platform, exportFormat);
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FileContentResult(bytes, contentType) { FileDownloadName = fileName };
    }

    public async Task<ActionResult<TopologyAuditResult>> GetAdminTopologyAsync(string endpointName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.AuditTopologyAsync(endpointName);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<TopologyCleanupResult>> PostAdminTopologyRemoveDeprecatedAsync(string endpointName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.RemoveDeprecatedTopologyAsync(endpointName);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkResubmitPreview>> GetAdminFailedPreviewAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewFailedMessagesAsync(endpointId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminBulkResubmitAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.BulkResubmitFailedAsync(endpointId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<Response2>> GetAdminDeadletteredPreviewAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var count = await _adminService.GetDeadLetteredCountAsync(endpointId);
        return new OkObjectResult(new Response2 { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteDeadletteredAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteDeadLetteredAsync(endpointId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<SessionPurgePreview>> GetAdminSessionPreviewAsync(string endpointId, string sessionId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewSessionPurgeAsync(endpointId, sessionId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<SessionPurgeResult>> PostAdminSessionPurgeAsync(string endpointId, string sessionId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointId,
                data: JsonConvert.SerializeObject(new { sessionId }));
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PurgeSessionAsync(endpointId, sessionId);
        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(new { sessionId }));
        return new OkObjectResult(result);
    }

    public async Task<IActionResult> DeleteAdminEventAsync(string endpointId, string eventId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var deleted = await _adminService.DeleteEventAsync(endpointId, eventId);
        if (deleted)
            return new OkResult();

        return new NotFoundResult();
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteAllAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteAllEventsAsync(endpointId);
        return new OkObjectResult(result);
    }

    // ───────────── Service Bus subscriptions ─────────────
    //
    // Listing is allowed for any topic in the namespace, including ones outside the
    // platform topology — the overview lists them deliberately, so a stray topic sitting
    // on a backlog is visible. Every mutation is gated on the topic being one NimBus owns:
    // acting on someone else's entity from here would be a surprise with no way back.

    public async Task<ActionResult<IEnumerable<ServiceBusTopicOverview>>> GetAdminServicebusTopicsAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var result = await _subscriptionAdminService.GetTopicOverviewAsync();
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<IEnumerable<ServiceBusSubscriptionInfo>>> GetAdminServicebusSubscriptionsAsync(string topicName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var result = await _subscriptionAdminService.GetSubscriptionsAsync(topicName);
        return new OkObjectResult(result);
    }

    public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionStatusAsync(
        SubscriptionStatusRequest body, string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName,
            body?.Action == SubscriptionStatusRequestAction.Enable ? "resume" : "pause",
            () => _subscriptionAdminService.SetSubscriptionStatusAsync(
                topicName, subscriptionName, body?.Action == SubscriptionStatusRequestAction.Enable));

    public async Task<ActionResult<BulkOperationResult>> PostAdminServicebusSubscriptionPurgeAsync(
        string topicName, string subscriptionName)
    {
        var denied = await GuardSubscriptionMutationAsync(topicName, subscriptionName, "purge");
        if (denied is not null) return denied;

        try
        {
            var result = await _subscriptionAdminService.PurgeSubscriptionAsync(topicName, subscriptionName);
            await LogSubscriptionAuditAsync(topicName, subscriptionName, "purge");
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
        catch (SubscriptionPurgeNotSupportedException exception)
        {
            return new BadRequestObjectResult(exception.Message);
        }
    }

    public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionRecreateAsync(
        string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, "recreate",
            () => _subscriptionAdminService.RecreateSubscriptionAsync(topicName, subscriptionName));

    public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusSubscriptionAsync(
        string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, "delete",
            () => _subscriptionAdminService.DeleteSubscriptionAsync(topicName, subscriptionName));

    public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusSubscriptionRuleAsync(
        string topicName, string subscriptionName, string ruleName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, $"detach-rule:{ruleName}",
            () => _subscriptionAdminService.DeleteRuleAsync(topicName, subscriptionName, ruleName));

    public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionRestoreRulesAsync(
        string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, "restore-rules",
            () => _subscriptionAdminService.RestoreRulesAsync(topicName, subscriptionName));

    /// <summary>
    /// Authorization, ownership and audit around one mutating subscription action, with the
    /// service's typed failures mapped to status codes rather than surfacing as a 500.
    /// </summary>
    private async Task<ActionResult<SubscriptionActionResult>> MutateSubscriptionAsync(
        string topicName,
        string subscriptionName,
        string action,
        Func<Task<SubscriptionActionResult>> mutate)
    {
        var denied = await GuardSubscriptionMutationAsync(topicName, subscriptionName, action);
        if (denied is not null) return denied;

        try
        {
            var result = await mutate();
            await LogSubscriptionAuditAsync(topicName, subscriptionName, action);
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
        catch (SubscriptionNotDescribableException exception)
        {
            return new BadRequestObjectResult(exception.Message);
        }
    }

    private async Task<ActionResult> GuardSubscriptionMutationAsync(string topicName, string subscriptionName, string action)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.ManageSubscription, _context,
                accessDenied: true,
                data: JsonConvert.SerializeObject(new { topicName, subscriptionName, action }));
            return new ForbidResult();
        }

        if (!IsPlatformTopic(topicName))
            return new NotFoundObjectResult($"Topic '{topicName}' is not part of the platform topology.");

        return null;
    }

    private Task LogSubscriptionAuditAsync(string topicName, string subscriptionName, string action) =>
        _auditLogService.LogAuditAsync(MessageAuditType.ManageSubscription, _context,
            data: JsonConvert.SerializeObject(new { topicName, subscriptionName, action }));

    private bool IsPlatformTopic(string topicName) =>
        TopologyDescriptor.IsSystemTopic(topicName)
        || EndpointVerificationService.EndpointExists(_platform, topicName);

    // ───────────── Advanced Operations ─────────────

    public async Task<ActionResult<PurgePreview>> PostAdminPurgePreviewAsync(string endpointId, PurgeRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionPreviewAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminPurgeAsync(string endpointId, PurgeRequest body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointId,
                data: JsonConvert.SerializeObject(body));
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(body));
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminDeleteByToPreviewAsync(DeleteByToRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        var count = await _adminService.DeleteMessagesByToPreviewAsync(body.ToField);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteByToAsync(DeleteByToRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        var result = await _adminService.DeleteMessagesByToAsync(body.ToField);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminDeleteByStatusPreviewAsync(string endpointId, DeleteByStatusRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        if (!AdminStatusValidation.TryNormalizeDeleteStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var count = await _adminService.DeleteByStatusPreviewAsync(endpointId, statuses);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteByStatusAsync(string endpointId, DeleteByStatusRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        if (!AdminStatusValidation.TryNormalizeDeleteStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var result = await _adminService.DeleteByStatusAsync(endpointId, statuses);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminSkipPreviewAsync(string endpointId, SkipRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        var before = body?.Before;
        if (!AdminStatusValidation.TryNormalizeSkipStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var count = await _adminService.SkipMessagesPreviewAsync(endpointId, statuses, before);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminSkipAsync(string endpointId, SkipRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        var before = body?.Before;
        if (!AdminStatusValidation.TryNormalizeSkipStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var result = await _adminService.SkipMessagesAsync(endpointId, statuses, before);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CopyResult>> PostAdminCopyAsync(string endpointId, CopyRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.CopyEndpointDataAsync(
            endpointId, body.TargetConnectionString,
            body.From, body.To,
            body.Statuses?.ToList() ?? new(), body.BatchSize);
        return new OkObjectResult(result);
    }

    // Every /api/admin/* operation is cross-endpoint and destructive-capable, so
    // the gate is the site Owner role (spec 026; the EIP_Management marker claim
    // still maps to site Owner via the authorization service's compat union).
    private Task<bool> IsSiteOwnerAsync() => _authorizationService.HasRoleAsync(AccessRole.Owner);
}

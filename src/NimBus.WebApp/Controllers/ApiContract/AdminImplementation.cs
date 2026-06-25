using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

public class AdminImplementation : IAdminApiController
{
    private readonly IAdminService _adminService;
    private readonly IPlatform _platform;
    private readonly IConfiguration _configuration;
    private readonly HttpContext _context;
    private readonly IAuditLogService _auditLogService;

    public AdminImplementation(
        IHttpContextAccessor contextAccessor,
        IAdminService adminService,
        IPlatform platform,
        IConfiguration configuration,
        IAuditLogService auditLogService)
    {
        _adminService = adminService;
        _platform = platform;
        _configuration = configuration;
        _context = contextAccessor.HttpContext;
        _auditLogService = auditLogService;
    }

    public async Task<ActionResult<PlatformConfig>> GetAdminPlatformConfigAsync()
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
            return new ForbidResult();

        var result = await _adminService.GetPlatformConfigAsync(_platform);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<TopologyAuditResult>> GetAdminTopologyAsync(string endpointName)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.AuditTopologyAsync(endpointName);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<TopologyCleanupResult>> PostAdminTopologyRemoveDeprecatedAsync(string endpointName)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointName,
                new { operation = "removeDeprecatedTopology", endpointName });
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.RemoveDeprecatedTopologyAsync(endpointName);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointName,
            new { operation = "removeDeprecatedTopology", endpointName });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkResubmitPreview>> GetAdminFailedPreviewAsync(string endpointId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewFailedMessagesAsync(endpointId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminBulkResubmitAsync(string endpointId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.Resubmit, denied: true, endpointId,
                new { operation = "bulkResubmit" });
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.BulkResubmitFailedAsync(endpointId);
        await AuditAdminAsync(MessageAuditType.Resubmit, denied: false, endpointId,
            new { operation = "bulkResubmit" });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<Response2>> GetAdminDeadletteredPreviewAsync(string endpointId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var count = await _adminService.GetDeadLetteredCountAsync(endpointId);
        return new OkObjectResult(new Response2 { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteDeadletteredAsync(string endpointId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId,
                new { operation = "deleteDeadlettered" });
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteDeadLetteredAsync(endpointId);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId,
            new { operation = "deleteDeadlettered" });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<SessionPurgePreview>> GetAdminSessionPreviewAsync(string endpointId, string sessionId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewSessionPurgeAsync(endpointId, sessionId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<SessionPurgeResult>> PostAdminSessionPurgeAsync(string endpointId, string sessionId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId,
                new { operation = "sessionPurge", sessionId });
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PurgeSessionAsync(endpointId, sessionId);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId,
            new { operation = "sessionPurge", sessionId });
        return new OkObjectResult(result);
    }

    public async Task<IActionResult> DeleteAdminEventAsync(string endpointId, string eventId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId,
                new { operation = "deleteEvent", eventId });
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var deleted = await _adminService.DeleteEventAsync(endpointId, eventId);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId,
            new { operation = "deleteEvent", eventId, deleted });
        if (deleted)
            return new OkResult();

        return new NotFoundResult();
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteAllAsync(string endpointId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId,
                new { operation = "deleteAll" });
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteAllEventsAsync(endpointId);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId,
            new { operation = "deleteAll" });
        return new OkObjectResult(result);
    }

    // ───────────── Advanced Operations ─────────────

    public async Task<ActionResult<PurgePreview>> PostAdminPurgePreviewAsync(string endpointId, PurgeRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionPreviewAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminPurgeAsync(string endpointId, PurgeRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId,
                new { operation = "purge", body });
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId,
            new { operation = "purge", body });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminDeleteByToPreviewAsync(DeleteByToRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        var count = await _adminService.DeleteMessagesByToPreviewAsync(body.ToField);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteByToAsync(DeleteByToRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId: null,
                new { operation = "deleteByTo", body.ToField });
            return new ForbidResult();
        }
        var result = await _adminService.DeleteMessagesByToAsync(body.ToField);
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId: null,
            new { operation = "deleteByTo", body.ToField });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminDeleteByStatusPreviewAsync(string endpointId, DeleteByStatusRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var count = await _adminService.DeleteByStatusPreviewAsync(endpointId, body.Statuses?.ToList() ?? new());
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteByStatusAsync(string endpointId, DeleteByStatusRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: true, endpointId,
                new { operation = "deleteByStatus", statuses = body.Statuses });
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteByStatusAsync(endpointId, body.Statuses?.ToList() ?? new());
        await AuditAdminAsync(MessageAuditType.PurgeMessages, denied: false, endpointId,
            new { operation = "deleteByStatus", statuses = body.Statuses });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminSkipPreviewAsync(string endpointId, SkipRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var count = await _adminService.SkipMessagesPreviewAsync(endpointId, body.Statuses?.ToList() ?? new(), body.Before);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminSkipAsync(string endpointId, SkipRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.Skip, denied: true, endpointId,
                new { operation = "skip", statuses = body.Statuses, before = body.Before });
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.SkipMessagesAsync(endpointId, body.Statuses?.ToList() ?? new(), body.Before);
        await AuditAdminAsync(MessageAuditType.Skip, denied: false, endpointId,
            new { operation = "skip", statuses = body.Statuses, before = body.Before });
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CopyResult>> PostAdminCopyAsync(string endpointId, CopyRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
        {
            await AuditAdminAsync(MessageAuditType.Resubmit, denied: true, endpointId,
                new { operation = "copy", from = body.From, to = body.To, statuses = body.Statuses });
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.CopyEndpointDataAsync(
            endpointId, body.TargetConnectionString,
            body.From, body.To,
            body.Statuses?.ToList() ?? new(), body.BatchSize);
        // Never persist the target connection string in the audit Data field.
        await AuditAdminAsync(MessageAuditType.Resubmit, denied: false, endpointId,
            new { operation = "copy", from = body.From, to = body.To, statuses = body.Statuses });
        return new OkObjectResult(result);
    }

    // Records a privileged admin action (and access-denied attempts) to the
    // central audit log (spec 008). The MessageAuditType is mapped to the
    // nearest existing semantic; the precise operation is always stamped into
    // the Data field via `operation` so the trail is unambiguous regardless of
    // the approximate type. The connection string for copy operations is
    // deliberately excluded from Data.
    private Task AuditAdminAsync(MessageAuditType type, bool denied, string? endpointId, object data) =>
        _auditLogService.LogAuditAsync(type, _context, accessDenied: denied, endpointId: endpointId,
            data: JsonConvert.SerializeObject(data));

    // Restrict the match to the "groups" claim type so non-group claims (e.g. scp,
    // preferred_username) whose value happens to contain the group name cannot
    // elevate privileges. Mirrors EndpointAuthorizationService.IsUserInGroup.
    private bool IsUserInSecurityGroup(string securityGrp)
    {
        var userClaims = _context.User.Identities.FirstOrDefault()?.Claims;
        return userClaims?.Any(c => c.Type == "groups" && c.Value == securityGrp) ?? false;
    }
}

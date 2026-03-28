using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NimBus.Core;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

public class AdminImplementation : IAdminApiController
{
    private readonly IAdminService _adminService;
    private readonly IPlatform _platform;
    private readonly IConfiguration _configuration;
    private readonly HttpContext _context;

    public AdminImplementation(
        IHttpContextAccessor contextAccessor,
        IAdminService adminService,
        IPlatform platform,
        IConfiguration configuration)
    {
        _adminService = adminService;
        _platform = platform;
        _configuration = configuration;
        _context = contextAccessor.HttpContext;
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
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.RemoveDeprecatedTopologyAsync(endpointName);
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
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.BulkResubmitFailedAsync(endpointId);
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
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteDeadLetteredAsync(endpointId);
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
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PurgeSessionAsync(endpointId, sessionId);
        return new OkObjectResult(result);
    }

    public async Task<IActionResult> DeleteAdminEventAsync(string endpointId, string eventId)
    {
        if (!IsUserInSecurityGroup("EIP_Management"))
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
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteAllEventsAsync(endpointId);
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
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
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
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        var result = await _adminService.DeleteMessagesByToAsync(body.ToField);
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
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteByStatusAsync(endpointId, body.Statuses?.ToList() ?? new());
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
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.SkipMessagesAsync(endpointId, body.Statuses?.ToList() ?? new(), body.Before);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CopyResult>> PostAdminCopyAsync(string endpointId, CopyRequest body)
    {
        if (!IsUserInSecurityGroup("EIP_Management")) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.CopyEndpointDataAsync(
            endpointId, body.TargetConnectionString,
            body.From, body.To,
            body.Statuses?.ToList() ?? new(), body.BatchSize);
        return new OkObjectResult(result);
    }

    private bool IsUserInSecurityGroup(string securityGrp)
    {
        var userClaims = _context.User.Identities.FirstOrDefault()?.Claims;
        return userClaims?.Any(c => c.Value == securityGrp) ?? false;
    }
}

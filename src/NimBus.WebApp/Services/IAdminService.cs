using System.Threading.Tasks;
using NimBus.Core;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

public interface IAdminService
{
    Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform);
    Task<TopologyAuditResult> AuditTopologyAsync(string endpointName);
    Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName);
    Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId);
    Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId);
    Task<int> GetDeadLetteredCountAsync(string endpointId);
    Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId);
    Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId);
    Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId);
    Task<bool> DeleteEventAsync(string endpointId, string eventId);
}

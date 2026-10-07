using Microsoft.AspNetCore.Mvc;

namespace NimBus.WebApp.ManagementApi;

/// <summary>
/// Admin → MCP access (Spec 037). The NSwag-generated controller is partial, so the antiforgery
/// requirement is declared here: every unsafe request (the policy save and turn-off) must carry
/// the <c>X-NimBus-CSRF</c> token that <c>GET /api/admin/mcp/settings</c> returns; GET is exempt.
/// </summary>
[AutoValidateAntiforgeryToken]
public partial class McpAccessApiController;

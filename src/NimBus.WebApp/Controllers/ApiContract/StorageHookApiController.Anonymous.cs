using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NimBus.WebApp.ManagementApi;

// Webhook endpoints are anonymous because Azure Event Grid cannot authenticate via cookies/tokens.
// Security is enforced via a shared webhook key validated on every request — sent in the
// X-Webhook-Key header (preferred; configure as an Event Grid custom delivery header) or the
// legacy ?key= query parameter. See StorageHookImplementation.ValidateWebhookKey.
[AllowAnonymous]
public partial class StorageHookApiController : Controller { }

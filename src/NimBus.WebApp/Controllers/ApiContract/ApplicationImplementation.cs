using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers.ApiContract
{
    // The stats endpoint is intentionally anonymous (health checks / status
    // monitoring); that exemption is applied per-action by
    // AllowAnonymousActionsConvention rather than class-level [AllowAnonymous],
    // so the other actions on ApplicationApiController (e.g. /api/me) stay
    // behind the global authorization filter. Its *payload* is authenticated-only
    // though (GH#93): environment name, exact version and backend topology let an
    // unauthenticated scanner fingerprint the deployment, so anonymous callers get
    // a bare liveness shape and only signed-in callers see the detail.
    public class ApplicationImplementation : IApplicationApiController
    {
        private readonly IConfiguration _config;
        private readonly IEndpointAuthorizationService _authService;
        private readonly IStorageProviderRegistration _storageProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ApplicationImplementation(
            IConfiguration config,
            IEndpointAuthorizationService authService,
            IStorageProviderRegistration storageProvider,
            IHttpContextAccessor httpContextAccessor)
        {
            _config = config;
            _authService = authService;
            _storageProvider = storageProvider;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<ActionResult<ApplicationStatus>> GetApiAppStatsAsync()
        {
            // GH#93: keep answering anonymously so liveness probes and status
            // monitors still work, but hand out nothing about the deployment.
            // The ambient principal is the same signal the global AuthorizeFilter
            // uses; GetCurrentUserName() is deliberately NOT used, because it is
            // null for an authenticated principal with no name claim.
            if (_httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated != true)
            {
                return new OkObjectResult(new ApplicationStatus());
            }

            var platformVersion = "TBD";
            var bhAssembly = Assembly.GetAssembly(typeof(PlatformConfiguration));
            if (bhAssembly != null)
            {
                var fileVersionInfo = FileVersionInfo.GetVersionInfo(bhAssembly.Location);
                var productVersion = fileVersionInfo.ProductVersion;
                platformVersion = productVersion?.Split("+")[0];
            }

            var statusResponse = new ApplicationStatus()
            {
                Env = _config.GetValue<string>("Environment"),
                PlatformVersion = platformVersion,
                StorageProvider = _storageProvider.ProviderName,
                // "{ticket}" placeholder URL template for reported-event deep
                // links; null/empty disables the link (plain badge).
                TicketLinkTemplate = _config.GetValue<string>("TicketLinkTemplate"),
            };

            return new OkObjectResult(statusResponse);
        }

        public Task<ActionResult<UserInfo>> GetMeAsync()
        {
            var name = _authService.GetCurrentUserName();
            return Task.FromResult<ActionResult<UserInfo>>(new OkObjectResult(new UserInfo { Name = name }));
        }
    }
}

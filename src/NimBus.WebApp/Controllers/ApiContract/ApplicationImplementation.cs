using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
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
    // behind the global authorization filter.
    public class ApplicationImplementation : IApplicationApiController
    {
        private readonly IConfiguration _config;
        private readonly IEndpointAuthorizationService _authService;
        private readonly IStorageProviderRegistration _storageProvider;

        public ApplicationImplementation(
            IConfiguration config,
            IEndpointAuthorizationService authService,
            IStorageProviderRegistration storageProvider)
        {
            _config = config;
            _authService = authService;
            _storageProvider = storageProvider;
        }

        public async Task<ActionResult<ApplicationStatus>> GetApiAppStatsAsync()
        {
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

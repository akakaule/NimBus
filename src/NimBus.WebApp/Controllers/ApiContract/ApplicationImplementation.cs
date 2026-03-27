using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Authorization;
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
    // Application status endpoint is intentionally anonymous to support health checks
    // and status monitoring without authentication.
    // The endpoint only returns non-sensitive information (environment name and version).
    namespace NimBus.WebApp.ManagementApi
    {
        [AllowAnonymous]
        public partial class ApplicationApiController : Controller { }
    }

    public class ApplicationImplementation : IApplicationApiController
    {
        private readonly IConfiguration _config;
        private readonly IEndpointAuthorizationService _authService;

        public ApplicationImplementation(IConfiguration config, IEndpointAuthorizationService authService)
        {
            _config = config;
            _authService = authService;
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
                
                PlatformVersion = platformVersion
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

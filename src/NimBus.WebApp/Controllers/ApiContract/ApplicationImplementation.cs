using NimBus.Core;
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
        private readonly IPlatform _platform;

        public ApplicationImplementation(
            IConfiguration config,
            IEndpointAuthorizationService authService,
            IStorageProviderRegistration storageProvider,
            IHttpContextAccessor httpContextAccessor,
            IPlatform platform)
        {
            _config = config;
            _authService = authService;
            _storageProvider = storageProvider;
            _httpContextAccessor = httpContextAccessor;
            _platform = platform;
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

            // The catalog package is whatever assembly the injected IPlatform came from:
            // the customer's package shipped by `nb deploy apps --platform-package`
            // (e.g. EET.Platform 1.0.1), or NimBus's own bundled sample catalog.
            var platformAssembly = _platform.GetType().Assembly;
            var nimbusAssembly = typeof(IPlatform).Assembly;

            var statusResponse = new ApplicationStatus()
            {
                Env = _config.GetValue<string>("Environment"),
                NimbusVersion = GetPackageVersion(nimbusAssembly),
                PlatformName = platformAssembly.GetName().Name,
                PlatformVersion = GetPackageVersion(platformAssembly),
                StorageProvider = _storageProvider.ProviderName,
                // "{ticket}" placeholder URL template for reported-event deep
                // links; null/empty disables the link (plain badge).
                TicketLinkTemplate = _config.GetValue<string>("TicketLinkTemplate"),
            };

            return new OkObjectResult(statusResponse);
        }

        /// <summary>
        /// The package version an assembly was built as: its informational version
        /// without the "+&lt;sha&gt;" source-revision suffix the .NET SDK appends, falling
        /// back to the assembly version.
        /// </summary>
        internal static string GetPackageVersion(Assembly assembly)
        {
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        public Task<ActionResult<UserInfo>> GetMeAsync()
        {
            var name = _authService.GetCurrentUserName();
            return Task.FromResult<ActionResult<UserInfo>>(new OkObjectResult(new UserInfo { Name = name }));
        }
    }
}

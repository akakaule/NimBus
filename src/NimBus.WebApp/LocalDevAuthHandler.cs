using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace NimBus.WebApp
{
    /// <summary>
    /// Authentication handler for local development that bypasses Azure AD authentication.
    /// This should only be used in Development environment with EnableLocalDevAuthentication=true.
    /// </summary>
    public class LocalDevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<LocalDevAuthHandler> _authLogger;

        public LocalDevAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration configuration)
            : base(options, logger, encoder)
        {
            _configuration = configuration;
            _authLogger = logger.CreateLogger<LocalDevAuthHandler>();
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Safety check: only allow bypass if explicitly enabled in configuration
            var enableLocalDevAuth = _configuration.GetValue<bool>("EnableLocalDevAuthentication", false);

            if (!enableLocalDevAuth)
            {
                _authLogger.LogWarning("Local development authentication bypass attempted but EnableLocalDevAuthentication is not enabled. Denying request.");
                return Task.FromResult(AuthenticateResult.Fail("Local development authentication is not enabled. Set EnableLocalDevAuthentication=true in configuration to enable."));
            }

            _authLogger.LogWarning("SECURITY WARNING: Local development authentication bypass is ENABLED. This should NEVER be used in production!");

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "local-dev-user"),
                new Claim(ClaimTypes.Name, "Local Developer"),
                new Claim(ClaimTypes.Email, "dev@localhost"),
                new Claim("groups", "EIP_Management") // Grant admin access for local development
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

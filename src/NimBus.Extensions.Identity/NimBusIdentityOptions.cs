namespace NimBus.Extensions.Identity;

/// <summary>
/// Configuration options for the NimBus Identity extension.
/// </summary>
public class NimBusIdentityOptions
{
    /// <summary>
    /// SQL Server connection string for the Identity database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Database schema for Identity tables. Default: "nimbus".
    /// </summary>
    public string Schema { get; set; } = "nimbus";

    /// <summary>
    /// Require email confirmation before allowing login. Default: true.
    /// </summary>
    public bool RequireEmailConfirmation { get; set; } = true;

    /// <summary>
    /// Show "Sign in with Microsoft" button alongside local login. Default: false.
    /// </summary>
    public bool EnableEntraIdLogin { get; set; }

    /// <summary>
    /// Allow unauthenticated visitors to self-register through
    /// <c>GET /account/register</c>. Default <c>false</c>: register routes
    /// return 404 and the login page hides the "Create an account" link.
    /// Set true on tenant slots where self-service signup is desired.
    /// </summary>
    public bool AllowRegistration { get; set; }

    /// <summary>
    /// SMTP configuration for sending confirmation and password reset emails.
    /// </summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>
    /// Deployment-time admin bootstrap. When Email and Password are both set and the user
    /// store is empty, a single confirmed admin is created on startup. Idempotent — no-op
    /// once any user exists. Intended for the very first sign-in on a fresh deployment;
    /// rotate or remove the password from configuration after that.
    /// </summary>
    public BootstrapOptions Bootstrap { get; set; } = new();
}

/// <summary>
/// First-admin bootstrap, applied by the Identity initializer hosted service.
/// </summary>
public class BootstrapOptions
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// SMTP configuration for the Identity email sender.
/// </summary>
public class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "NimBus";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
}

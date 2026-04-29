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
    /// SMTP configuration for sending confirmation and password reset emails.
    /// </summary>
    public SmtpOptions Smtp { get; set; } = new();
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

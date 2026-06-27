namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Selects the delivery mechanism used by <see cref="EmailChannel"/>.
    /// </summary>
    public enum EmailProvider
    {
        /// <summary>Deliver via the SendGrid HTTPS API (requires an API key).</summary>
        SendGrid,

        /// <summary>Deliver via an SMTP relay using <see cref="System.Net.Mail.SmtpClient"/>.</summary>
        Smtp,
    }
}

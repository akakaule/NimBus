namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Substitutes <c>{Placeholder}</c> tokens in a template string with values from a
    /// <see cref="Notification"/>. Unknown placeholders are left literal; missing values
    /// resolve to an empty string.
    /// </summary>
    public static class TemplateRenderer
    {
        /// <summary>
        /// Renders <paramref name="template"/> against <paramref name="notification"/>, replacing the
        /// supported tokens: <c>{Severity}</c>, <c>{Title}</c>, <c>{Message}</c>, <c>{EventId}</c>,
        /// <c>{EventTypeId}</c>, <c>{MessageId}</c>, <c>{CorrelationId}</c>, <c>{ErrorDetails}</c>.
        /// </summary>
        public static string Render(string template, Notification notification)
        {
            if (string.IsNullOrEmpty(template) || notification == null)
            {
                return template ?? string.Empty;
            }

            return template
                .Replace("{Severity}", notification.Severity.ToString())
                .Replace("{Title}", notification.Title ?? string.Empty)
                .Replace("{Message}", notification.Message ?? string.Empty)
                .Replace("{EventId}", notification.EventId ?? string.Empty)
                .Replace("{EventTypeId}", notification.EventTypeId ?? string.Empty)
                .Replace("{MessageId}", notification.MessageId ?? string.Empty)
                .Replace("{CorrelationId}", notification.CorrelationId ?? string.Empty)
                .Replace("{ErrorDetails}", notification.ErrorDetails ?? string.Empty);
        }
    }
}

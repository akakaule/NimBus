namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// Options for the fallback <see cref="ConsoleNotificationChannel"/> registered when the fluent
    /// channel builder configures no channels. Carries only a <see cref="NotificationChannelOptions.MinSeverity"/>;
    /// there is nothing to validate.
    /// </summary>
    internal sealed class ConsoleChannelOptions : NotificationChannelOptions
    {
        internal override void Validate()
        {
            // No required configuration for the console fallback.
        }
    }
}

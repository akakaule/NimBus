using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications
{
    /// <summary>
    /// A simple notification channel that writes to the console.
    /// Useful for development and debugging.
    /// </summary>
    public class ConsoleNotificationChannel : INotificationChannel
    {
        public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            var color = notification.Severity switch
            {
                NotificationSeverity.Critical => ConsoleColor.Red,
                NotificationSeverity.Error => ConsoleColor.DarkRed,
                NotificationSeverity.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.Cyan,
            };

            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[NimBus Notification] [{notification.Severity}] {notification.Title}");
            Console.WriteLine($"  {notification.Message}");
            if (!string.IsNullOrEmpty(notification.ErrorDetails))
            {
                Console.WriteLine($"  Error: {notification.ErrorDetails}");
            }
            Console.ForegroundColor = originalColor;

            return Task.CompletedTask;
        }
    }
}

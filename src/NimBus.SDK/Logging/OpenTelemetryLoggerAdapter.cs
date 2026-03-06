using System;
using Microsoft.Extensions.Logging;

namespace NimBus.SDK.Logging
{
    /// <summary>
    /// Bridges the platform's Core.Logging.ILogger to Microsoft.Extensions.Logging.ILogger,
    /// enabling OpenTelemetry export via the standard .NET logging pipeline.
    /// </summary>
    public class OpenTelemetryLoggerAdapter : Core.Logging.ILogger
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        public OpenTelemetryLoggerAdapter(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger;
        }

        public void Verbose(string messageTemplate, params object[] propertyValues) =>
            _logger.LogTrace(messageTemplate, propertyValues);

        public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) =>
            _logger.LogTrace(exception, messageTemplate, propertyValues);

        public void Information(string messageTemplate, params object[] propertyValues) =>
            _logger.LogInformation(messageTemplate, propertyValues);

        public void Information(Exception exception, string messageTemplate, params object[] propertyValues) =>
            _logger.LogInformation(exception, messageTemplate, propertyValues);

        public void Error(string messageTemplate, params object[] propertyValues) =>
            _logger.LogError(messageTemplate, propertyValues);

        public void Error(Exception exception, string messageTemplate, params object[] propertyValues) =>
            _logger.LogError(exception, messageTemplate, propertyValues);

        public void Fatal(string messageTemplate, params object[] propertyValues) =>
            _logger.LogCritical(messageTemplate, propertyValues);

        public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) =>
            _logger.LogCritical(exception, messageTemplate, propertyValues);
    }
}

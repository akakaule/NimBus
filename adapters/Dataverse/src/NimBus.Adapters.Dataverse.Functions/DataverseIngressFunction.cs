using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace NimBus.Adapters.Dataverse.Functions;

/// <summary>Non-session raw Dataverse ingress; NimBus sessions begin at publication.</summary>
public sealed class DataverseIngressFunction(DataverseIngress ingress, DataverseOptions options, ILogger<DataverseIngressFunction> logger, TelemetryClient telemetry)
{
    private static readonly Action<ILogger, string, Exception?> LogRejected = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1), "Dataverse input rejected: {Reason}");
    private static readonly Action<ILogger, Exception?> LogCompleted = LoggerMessage.Define(LogLevel.Information, new EventId(2), "Dataverse notification published and input completed.");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Publish and then settle the trigger message. Runtime failures propagate for redelivery.</summary>
    [Function("DataverseIngress")]
    public async Task RunAsync(
        [ServiceBusTrigger("%DataverseQueue%", Connection = "DataverseServiceBus", IsSessionsEnabled = false, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        telemetry.GetMetric("dataverse.received").TrackValue(1);
        async Task Reject(string reason, CancellationToken token)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: reason,
                deadLetterErrorDescription: "See the Dataverse adapter runbook for this reason code.", cancellationToken: token).ConfigureAwait(false);
            telemetry.GetMetric("dataverse.rejected").TrackValue(1);
            LogRejected(logger, reason, null);
        }

        var bytes = message.Body.ToMemory();
        if (bytes.Length > options.MaxBodyBytes)
        {
            await Reject("ContextTooLarge", cancellationToken).ConfigureAwait(false);
            return;
        }

        string body;
        try { body = StrictUtf8.GetString(bytes.Span); }
        catch (DecoderFallbackException)
        {
            await Reject("InvalidUtf8", cancellationToken).ConfigureAwait(false);
            return;
        }

        await ingress.ProcessAsync(body, message.ApplicationProperties.ContainsKey("MessageMaxSizeExceeded"),
            async token =>
            {
                await actions.CompleteMessageAsync(message, token).ConfigureAwait(false);
                telemetry.GetMetric("dataverse.published_and_completed").TrackValue(1);
                LogCompleted(logger, null);
            }, Reject, cancellationToken).ConfigureAwait(false);
    }
}

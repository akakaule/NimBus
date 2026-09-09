using NimBus.SDK;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NimBus.Adapters.Dataverse.Contracts;
using NimBus.Core.Messages;
namespace NimBus.Adapters.Dataverse;

/// <summary>Publishes before settling input; transient failures remain eligible for redelivery.</summary>
public sealed class DataverseIngress(DataverseContextReader reader, IPublisherClient publisher, DataverseOptions options)
{
    /// <summary>Process a single input using host-owned settlement operations.</summary>
    public async Task ProcessAsync(string body, bool truncated, Func<CancellationToken, Task> complete,
        Func<string, CancellationToken, Task> deadLetter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataverseRecordEvent record;
        try { record = reader.Read(body, truncated); }
        catch (DataverseInputException exception)
        {
            await deadLetter(exception.Reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var eventType = record.GetEventType().Id;
        var identity = $"{record.OrganizationId:D}/{record.RegistrationId:D}/{record.OperationId:D}/{record.Table}/{record.RecordId:D}/{eventType}";
        var messageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var message = new Message
        {
            MessageId = messageId,
            To = eventType,
            EventTypeId = eventType,
            SessionId = record.SessionId,
            CorrelationId = record.CorrelationId.ToString("D"),
            OriginatingFrom = options.PublisherEndpoint,
            From = options.PublisherEndpoint,
            MessageType = MessageType.EventRequest,
            RetryCount = 0,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = eventType, EventJson = JsonConvert.SerializeObject(record) },
            },
        };
        if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(message.MessageContent)) > 192 * 1024)
        {
            await deadLetter("OutputTooLarge", cancellationToken).ConfigureAwait(false);
            return;
        }
        // Keep send failures outside the permanent-input catch. A host/transport failure is retryable.
        await publisher.Publish(message, cancellationToken).ConfigureAwait(false);
        await complete(cancellationToken).ConfigureAwait(false);
    }
}

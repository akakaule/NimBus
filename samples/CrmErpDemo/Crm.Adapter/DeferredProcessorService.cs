using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace Crm.Adapter;

/// <summary>
/// Background service that processes deferred messages for the CRM endpoint.
/// Mirrors AspirePubSub.Subscriber.DeferredProcessorService: listens on the
/// DeferredProcessor subscription (sessions=OFF) and replays deferred messages
/// in FIFO order once a session is unblocked.
/// </summary>
public sealed class DeferredProcessorService(
    ServiceBusClient serviceBusClient,
    IDeferredMessageProcessor deferredMessageProcessor,
    ILogger<DeferredProcessorService> logger,
    string topicName,
    string subscriptionName = "deferredprocessor") : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DeferredProcessorService starting for {Topic}/{Subscription}", topicName, subscriptionName);

        var processor = serviceBusClient.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false,
        });

        processor.ProcessMessageAsync += async args =>
        {
            var message = args.Message;
            var sessionId = message.SessionId ?? message.ApplicationProperties.GetValueOrDefault("SessionId")?.ToString();

            if (string.IsNullOrEmpty(sessionId))
            {
                await args.DeadLetterMessageAsync(message, "No SessionId", cancellationToken: stoppingToken);
                return;
            }

            try
            {
                await deferredMessageProcessor.ProcessDeferredMessagesAsync(sessionId, topicName, stoppingToken);
                await args.CompleteMessageAsync(message, stoppingToken);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                await args.CompleteMessageAsync(message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process deferred messages for session {SessionId}", sessionId);
                await args.AbandonMessageAsync(message, cancellationToken: stoppingToken);
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "DeferredProcessor error on {Topic}/{Subscription}", topicName, subscriptionName);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        await processor.StopProcessingAsync();
    }
}

using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace AspirePubSub.Subscriber;

/// <summary>
/// Background service that processes deferred messages for this endpoint.
/// Listens on the DeferredProcessor subscription (sessions=OFF) and calls
/// DeferredMessageProcessor to republish deferred messages in FIFO order.
///
/// This is the separated deferred processing pattern — the main subscriber handler
/// (StrictMessageHandler) does NOT handle ProcessDeferredRequest. Instead, this
/// dedicated service handles it independently.
/// </summary>
public sealed class DeferredProcessorService : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IDeferredMessageProcessor _deferredMessageProcessor;
    private readonly ILogger<DeferredProcessorService> _logger;
    private readonly string _topicName;
    private readonly string _subscriptionName;

    public DeferredProcessorService(
        ServiceBusClient serviceBusClient,
        IDeferredMessageProcessor deferredMessageProcessor,
        ILogger<DeferredProcessorService> logger,
        string topicName,
        string subscriptionName = "deferredprocessor")
    {
        _serviceBusClient = serviceBusClient;
        _deferredMessageProcessor = deferredMessageProcessor;
        _logger = logger;
        _topicName = topicName;
        _subscriptionName = subscriptionName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeferredProcessorService starting for {Topic}/{Subscription}", _topicName, _subscriptionName);

        // Create a processor for the DeferredProcessor subscription (sessions=OFF)
        var processor = _serviceBusClient.CreateProcessor(_topicName, _subscriptionName, new ServiceBusProcessorOptions
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
                _logger.LogWarning("ProcessDeferredRequest message has no SessionId, dead-lettering");
                await args.DeadLetterMessageAsync(message, "No SessionId", cancellationToken: stoppingToken);
                return;
            }

            _logger.LogInformation("Processing deferred messages for session {SessionId} on topic {Topic}", sessionId, _topicName);

            try
            {
                await _deferredMessageProcessor.ProcessDeferredMessagesAsync(sessionId, _topicName, stoppingToken);
                await args.CompleteMessageAsync(message, stoppingToken);
                _logger.LogInformation("Successfully processed deferred messages for session {SessionId}", sessionId);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                // No deferred messages for this session — complete the trigger
                _logger.LogInformation("No deferred messages found for session {SessionId} (session cannot be locked)", sessionId);
                await args.CompleteMessageAsync(message, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process deferred messages for session {SessionId}", sessionId);
                // Let Service Bus retry by not completing
                await args.AbandonMessageAsync(message, cancellationToken: stoppingToken);
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "DeferredProcessor error on {Topic}/{Subscription}", _topicName, _subscriptionName);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);

        // Wait until cancellation is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        await processor.StopProcessingAsync();
        _logger.LogInformation("DeferredProcessorService stopped");
    }
}

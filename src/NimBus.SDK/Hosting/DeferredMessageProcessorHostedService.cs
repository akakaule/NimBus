using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// Worker-side host for the deferred-processor trigger. Wraps a non-session
    /// <see cref="ServiceBusProcessor"/> on the <c>deferredprocessor</c>
    /// subscription and delegates each trigger to
    /// <see cref="DeferredMessageDispatcher"/>. Registered by
    /// <c>AddNimBusSubscriber</c> via <c>TryAddEnumerable</c>; suppress with
    /// <c>NimBusSubscriberOptions.DisableDeferredProcessorHostedService = true</c>
    /// when the host owns the trigger directly (e.g. Azure Functions
    /// <c>[ServiceBusTrigger]</c>).
    /// </summary>
    internal sealed class DeferredMessageProcessorHostedService : BackgroundService
    {
        private readonly ServiceBusClient _serviceBusClient;
        private readonly IDeferredMessageProcessor _deferredMessageProcessor;
        private readonly DeferredMessageProcessorHostedServiceOptions _options;
        private readonly ILogger<DeferredMessageProcessorHostedService> _logger;

        public DeferredMessageProcessorHostedService(
            ServiceBusClient serviceBusClient,
            IDeferredMessageProcessor deferredMessageProcessor,
            DeferredMessageProcessorHostedServiceOptions options,
            ILogger<DeferredMessageProcessorHostedService> logger)
        {
            _serviceBusClient = serviceBusClient ?? throw new ArgumentNullException(nameof(serviceBusClient));
            _deferredMessageProcessor = deferredMessageProcessor ?? throw new ArgumentNullException(nameof(deferredMessageProcessor));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Deferred-processor hosted service enabled on topic '{Topic}' (subscription '{Subscription}', MaxConcurrentCalls {MaxConcurrentCalls}). " +
                "Set NimBusSubscriberOptions.DisableDeferredProcessorHostedService = true to opt out.",
                _options.TopicName, _options.SubscriptionName, _options.MaxConcurrentCalls);

            var processor = _serviceBusClient.CreateProcessor(
                _options.TopicName,
                _options.SubscriptionName,
                new ServiceBusProcessorOptions
                {
                    // Default 1: the trigger subscription is non-session, so
                    // serial processing is the only ordering mechanism. See
                    // DeferredMessageProcessorHostedServiceOptions.MaxConcurrentCalls.
                    MaxConcurrentCalls = _options.MaxConcurrentCalls,
                    AutoCompleteMessages = false,
                });

            processor.ProcessMessageAsync += args => OnMessageAsync(args, stoppingToken);
            processor.ProcessErrorAsync += OnErrorAsync;

            await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected on shutdown.
            }
            finally
            {
                // Use CancellationToken.None so the stop completes cleanly even when
                // stoppingToken is already cancelled.
                await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
                await processor.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task OnMessageAsync(ProcessMessageEventArgs args, CancellationToken stoppingToken)
        {
            try
            {
                var outcome = await DeferredMessageDispatcher.ProcessAsync(
                    args.Message, _deferredMessageProcessor, _options.TopicName, args.CancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Action == DeferredMessageDispatchAction.DeadLetter)
                {
                    await args.DeadLetterMessageAsync(args.Message, outcome.DeadLetterReason, cancellationToken: args.CancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down. Do not abandon — let the lock expire
                // naturally and exit the message pump. Abandoning here would
                // treat shutdown as a failure and trigger redelivery on the
                // next host start.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process deferred-processor trigger on {Topic}/{Subscription} for session {SessionId}",
                    _options.TopicName, _options.SubscriptionName, args.Message.SessionId);

                // CancellationToken.None: stoppingToken may already be cancelled
                // mid-shutdown if a real error tripped us. We still want the
                // abandon to complete so redelivery is correct.
                try
                {
                    await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception abandonEx)
                {
                    _logger.LogError(abandonEx,
                        "Failed to abandon deferred-processor trigger after handler exception on {Topic}/{Subscription}",
                        _options.TopicName, _options.SubscriptionName);
                }
            }
        }

        private Task OnErrorAsync(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception,
                "Deferred-processor service-bus error on {Topic}/{Subscription} (source: {ErrorSource})",
                _options.TopicName, _options.SubscriptionName, args.ErrorSource);
            return Task.CompletedTask;
        }
    }
}

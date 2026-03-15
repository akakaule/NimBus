using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.ServiceBus;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// Background service that receives messages from a Service Bus topic/subscription
    /// using a <see cref="ServiceBusSessionProcessor"/> and delegates to <see cref="IServiceBusAdapter"/>.
    /// </summary>
    public class NimBusReceiverHostedService : BackgroundService
    {
        private readonly ServiceBusClient _client;
        private readonly IServiceBusAdapter _adapter;
        private readonly NimBusReceiverOptions _options;
        private readonly ILogger<NimBusReceiverHostedService> _logger;
        private ServiceBusSessionProcessor _processor;

        public NimBusReceiverHostedService(
            ServiceBusClient client,
            IServiceBusAdapter adapter,
            NimBusReceiverOptions options,
            ILogger<NimBusReceiverHostedService> logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var processorOptions = new ServiceBusSessionProcessorOptions
            {
                MaxConcurrentSessions = _options.MaxConcurrentSessions,
                MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
                AutoCompleteMessages = false,
            };

            _processor = _client.CreateSessionProcessor(
                _options.TopicName,
                _options.SubscriptionName,
                processorOptions);

            _processor.ProcessMessageAsync += OnMessageAsync;
            _processor.ProcessErrorAsync += OnErrorAsync;

            _logger.LogInformation(
                "Starting NimBus receiver for {Topic}/{Subscription} (MaxConcurrentSessions={MaxSessions})",
                _options.TopicName, _options.SubscriptionName, _options.MaxConcurrentSessions);

            await _processor.StartProcessingAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // The processor runs via event callbacks; just keep alive until cancelled
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping NimBus receiver for {Topic}/{Subscription}",
                _options.TopicName, _options.SubscriptionName);

            if (_processor != null)
            {
                await _processor.StopProcessingAsync(cancellationToken);
                await _processor.DisposeAsync();
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task OnMessageAsync(ProcessSessionMessageEventArgs args)
        {
            await _adapter.Handle(args, args.CancellationToken);
        }

        private Task OnErrorAsync(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception,
                "Error processing message. Source: {ErrorSource}, Namespace: {Namespace}, EntityPath: {EntityPath}",
                args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);

            return Task.CompletedTask;
        }
    }
}

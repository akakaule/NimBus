using Microsoft.Extensions.Hosting;
using NimBus.Core.Outbox;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// Background service that polls the outbox and dispatches pending messages to Service Bus.
    /// </summary>
    public class OutboxDispatcherHostedService : BackgroundService
    {
        private readonly OutboxDispatcher _dispatcher;
        private readonly TimeSpan _pollingInterval;
        private readonly int _batchSize;

        public OutboxDispatcherHostedService(OutboxDispatcher dispatcher, TimeSpan pollingInterval, int batchSize)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _pollingInterval = pollingInterval;
            _batchSize = batchSize;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var dispatched = await _dispatcher.DispatchPendingAsync(_batchSize, stoppingToken);

                    // If we dispatched a full batch, immediately poll again (more may be waiting)
                    if (dispatched >= _batchSize)
                        continue;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Log and continue - transient failures should not stop the dispatcher
                }

                await Task.Delay(_pollingInterval, stoppingToken);
            }
        }
    }
}

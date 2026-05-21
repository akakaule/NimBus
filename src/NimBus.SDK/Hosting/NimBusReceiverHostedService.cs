using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.ServiceBus;
using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
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
        private readonly object _errorGate = new object();
        private ServiceBusSessionProcessor? _processor;
        private readonly TaskCompletionSource<bool> _startupCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<ProcessorRecoveryRequest> _processorRecoveryRequested =
            new TaskCompletionSource<ProcessorRecoveryRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _recoverableErrorCount;
        private DateTimeOffset _firstRecoverableErrorAt;

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
            ValidateOptions(_options);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            await _startupCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            RunProcessorLoopAsync(stoppingToken);

        internal async Task RunProcessorLoopAsync(CancellationToken stoppingToken)
        {
            var restartCount = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                ResetRecoverableErrors();
                ResetRecoverySignal();

                _processor = CreateProcessor();

                try
                {
                    _logger.LogInformation(
                        "Starting NimBus receiver for {Topic}/{Subscription} (MaxConcurrentSessions={MaxSessions})",
                        _options.TopicName, _options.SubscriptionName, _options.MaxConcurrentSessions);

                    await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
                    _startupCompletion.TrySetResult(true);

                    var completed = await Task.WhenAny(
                        _processorRecoveryRequested.Task,
                        Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)).ConfigureAwait(false);

                    if (completed != _processorRecoveryRequested.Task)
                    {
                        break;
                    }

                    var recovery = await _processorRecoveryRequested.Task.ConfigureAwait(false);
                    restartCount++;

                    _logger.LogWarning(
                        recovery.Exception,
                        "Restarting NimBus receiver for {Topic}/{Subscription} after {ErrorCount} consecutive recoverable processor errors. RestartCount={RestartCount}, LastErrorSource={ErrorSource}",
                        _options.TopicName,
                        _options.SubscriptionName,
                        recovery.ErrorCount,
                        restartCount,
                        recovery.ErrorSource);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _startupCompletion.TrySetCanceled(stoppingToken);
                    break;
                }
                catch (Exception ex)
                {
                    _startupCompletion.TrySetException(ex);
                    throw;
                }
                finally
                {
                    await StopAndDisposeProcessorAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (!stoppingToken.IsCancellationRequested && _options.ProcessorRestartDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.ProcessorRestartDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping NimBus receiver for {Topic}/{Subscription}",
                _options.TopicName, _options.SubscriptionName);

            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            await StopAndDisposeProcessorAsync(cancellationToken).ConfigureAwait(false);
        }

        private ServiceBusSessionProcessor CreateProcessor()
        {
            var processorOptions = new ServiceBusSessionProcessorOptions
            {
                MaxConcurrentSessions = _options.MaxConcurrentSessions,
                MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
                SessionIdleTimeout = _options.SessionIdleTimeout,
                AutoCompleteMessages = false,
            };

            var processor = _client.CreateSessionProcessor(
                _options.TopicName,
                _options.SubscriptionName,
                processorOptions);

            processor.ProcessMessageAsync += OnMessageAsync;
            processor.ProcessErrorAsync += OnErrorAsync;

            return processor;
        }

        private async Task StopAndDisposeProcessorAsync(CancellationToken cancellationToken)
        {
            var processor = Interlocked.Exchange(ref _processor, null);
            if (processor == null)
            {
                return;
            }

            processor.ProcessMessageAsync -= OnMessageAsync;
            processor.ProcessErrorAsync -= OnErrorAsync;

            try
            {
                await processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Error while stopping NimBus receiver for {Topic}/{Subscription}",
                    _options.TopicName,
                    _options.SubscriptionName);
            }

            await DisposeProcessorAsync(processor).ConfigureAwait(false);
        }

        protected virtual ValueTask DisposeProcessorAsync(ServiceBusSessionProcessor processor) =>
            processor.DisposeAsync();

        private async Task OnMessageAsync(ProcessSessionMessageEventArgs args)
        {
            await _adapter.Handle(args, args.CancellationToken).ConfigureAwait(false);
            ResetRecoverableErrors();
        }

        private Task OnErrorAsync(ProcessErrorEventArgs args) =>
            HandleProcessorErrorAsync(args);

        internal async Task HandleProcessorErrorAsync(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception,
                "Error processing message. Source: {ErrorSource}, Namespace: {Namespace}, EntityPath: {EntityPath}",
                args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);

            if (!IsRecoverableProcessorInfrastructureError(args))
            {
                return;
            }

            var state = RecordRecoverableError(args);

            if (state.ShouldRestart)
            {
                _processorRecoveryRequested.TrySetResult(new ProcessorRecoveryRequest(
                    args.Exception,
                    args.ErrorSource,
                    state.ErrorCount));
            }

            if (state.Delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(state.Delay, args.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The processor is stopping or restarting.
                }
            }
        }

        private RecoverableErrorState RecordRecoverableError(ProcessErrorEventArgs args)
        {
            lock (_errorGate)
            {
                var now = DateTimeOffset.UtcNow;
                if (_recoverableErrorCount == 0 || now - _firstRecoverableErrorAt > _options.RecoverableErrorWindow)
                {
                    _firstRecoverableErrorAt = now;
                    _recoverableErrorCount = 0;
                }

                _recoverableErrorCount++;

                return new RecoverableErrorState(
                    _recoverableErrorCount,
                    CalculateRecoverableErrorDelay(_recoverableErrorCount),
                    _recoverableErrorCount >= _options.RecoverableErrorRestartThreshold);
            }
        }

        private TimeSpan CalculateRecoverableErrorDelay(int errorCount)
        {
            if (_options.RecoverableErrorDelay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var multiplier = Math.Pow(2, Math.Min(errorCount - 1, 5));
            var delayTicks = _options.RecoverableErrorDelay.Ticks * multiplier;
            if (delayTicks >= _options.MaxRecoverableErrorDelay.Ticks)
            {
                return _options.MaxRecoverableErrorDelay;
            }

            return TimeSpan.FromTicks((long)delayTicks);
        }

        private void ResetRecoverableErrors()
        {
            lock (_errorGate)
            {
                _recoverableErrorCount = 0;
                _firstRecoverableErrorAt = default;
            }
        }

        private void ResetRecoverySignal()
        {
            _processorRecoveryRequested = new TaskCompletionSource<ProcessorRecoveryRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static bool IsRecoverableProcessorInfrastructureError(ProcessErrorEventArgs args)
        {
            if (args.ErrorSource == ServiceBusErrorSource.ProcessMessageCallback)
            {
                return false;
            }

            if (args.Exception is ObjectDisposedException)
            {
                return true;
            }

            if (args.Exception is ServiceBusException serviceBusException)
            {
                return serviceBusException.Reason == ServiceBusFailureReason.ServiceCommunicationProblem
                    || serviceBusException.Reason == ServiceBusFailureReason.ServiceTimeout
                    || serviceBusException.Reason == ServiceBusFailureReason.ServiceBusy
                    || serviceBusException.Reason == ServiceBusFailureReason.GeneralError;
            }

            return ContainsException<SocketException>(args.Exception)
                || ContainsException<WebSocketException>(args.Exception)
                || ContainsException<IOException>(args.Exception);
        }

        private static bool ContainsException<TException>(Exception exception)
            where TException : Exception
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is TException)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateOptions(NimBusReceiverOptions options)
        {
            if (options.MaxConcurrentSessions <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentSessions), "MaxConcurrentSessions must be greater than zero.");
            }

            if (options.MaxAutoLockRenewalDuration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaxAutoLockRenewalDuration), "MaxAutoLockRenewalDuration cannot be negative.");
            }

            if (options.SessionIdleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options.SessionIdleTimeout), "SessionIdleTimeout must be greater than zero.");
            }

            if (options.RecoverableErrorRestartThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.RecoverableErrorRestartThreshold), "RecoverableErrorRestartThreshold must be greater than zero.");
            }

            if (options.RecoverableErrorWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options.RecoverableErrorWindow), "RecoverableErrorWindow must be greater than zero.");
            }

            if (options.MaxRecoverableErrorDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MaxRecoverableErrorDelay), "MaxRecoverableErrorDelay cannot be negative.");
            }

            if (options.RecoverableErrorDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options.RecoverableErrorDelay), "RecoverableErrorDelay cannot be negative.");
            }

            if (options.ProcessorRestartDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options.ProcessorRestartDelay), "ProcessorRestartDelay cannot be negative.");
            }
        }

        private readonly struct RecoverableErrorState
        {
            public RecoverableErrorState(int errorCount, TimeSpan delay, bool shouldRestart)
            {
                ErrorCount = errorCount;
                Delay = delay;
                ShouldRestart = shouldRestart;
            }

            public int ErrorCount { get; }
            public TimeSpan Delay { get; }
            public bool ShouldRestart { get; }
        }

        private readonly struct ProcessorRecoveryRequest
        {
            public ProcessorRecoveryRequest(Exception exception, ServiceBusErrorSource errorSource, int errorCount)
            {
                Exception = exception;
                ErrorSource = errorSource;
                ErrorCount = errorCount;
            }

            public Exception Exception { get; }
            public ServiceBusErrorSource ErrorSource { get; }
            public int ErrorCount { get; }
        }
    }
}

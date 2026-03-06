using NimBus.Core.Logging;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages
{
    public class StrictMessageHandler : MessageHandler
    {
        private readonly IEventContextHandler _eventContextHandler;
        private readonly IResponseService _responseService;
        private readonly IDeferredMessageProcessor _deferredMessageProcessor;
        private readonly string _topicName;

        public StrictMessageHandler(IEventContextHandler eventContextHandler, IResponseService responseService, ILoggerProvider loggerProvider) : base(loggerProvider)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
        }

        public StrictMessageHandler(
            IEventContextHandler eventContextHandler,
            IResponseService responseService,
            ILoggerProvider loggerProvider,
            IDeferredMessageProcessor deferredMessageProcessor,
            string topicName) : base(loggerProvider)
        {
            _eventContextHandler = eventContextHandler;
            _responseService = responseService;
            _deferredMessageProcessor = deferredMessageProcessor;
            _topicName = topicName;
        }

        public override async Task HandleEventRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfoWithMessageMetaData(logger, messageContext, "Handle");

                if (messageContext.EventTypeId == "Heartbeat" && messageContext.MessageType == MessageType.EventRequest)
                {
                    await SendResolutionResponse(messageContext, cancellationToken);
                }
                else
                {
                    await VerifySessionIsNotBlocked(messageContext, cancellationToken);
                    await HandleEventContent(messageContext, logger, cancellationToken);
                    await SendResolutionResponse(messageContext, cancellationToken);
                }
                await CompleteMessage(messageContext, cancellationToken);
                LogInfoWithMessageMetaData(logger, messageContext, "Successfully processed");
            }
            catch (EventHandlerNotFoundException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event", exception);
                await SendUnsupportedResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (SessionBlockedException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event", exception);
                // Session is blocked (by failed event request, or by deferred event requests).
                await SendDeferralResponse(messageContext, exception, cancellationToken);
                await DeferMessageToSubscription(messageContext, cancellationToken);
                throw;
            }
            catch (EventContextHandlerException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event", exception);
                // Event handler threw (non-transient) exception.
                await SendErrorResponse(messageContext, exception, cancellationToken);
                await BlockSession(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                await CheckForRetry(messageContext, exception, cancellationToken);
                throw;
            }
        }


        public override async Task HandleRetryRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfoWithMessageMetaData(logger, messageContext, "Handle (RetryRequest)");
                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                await HandleEventContent(messageContext, logger, cancellationToken);
                await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, logger, cancellationToken);
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                LogInfoWithMessageMetaData(logger, messageContext, "Successfully processed (RetryRequest)");
            }
            catch (SessionBlockedException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event (RetryRequest)", exception);
                // Session is not blocked by this, so the resubmitted event must have already been resolved.
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (EventContextHandlerException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event (RetryRequest)", exception);
                // Event handler threw (non-transient) exception.
                await SendErrorResponse(messageContext, exception, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                await CheckForRetry(messageContext, exception, cancellationToken);
            }
        }

        public override async Task HandleResubmissionRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfoWithMessageMetaData(logger, messageContext, "Handle (Resubmission)");
                AuthorizeManagerRequest(messageContext);
                await HandleEventContent(messageContext, logger, cancellationToken);
                if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                    await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, logger, cancellationToken);
                await SendResolutionResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
                LogInfoWithMessageMetaData(logger, messageContext, "Successfully processed (Resubmission)");
            }
            catch (EventHandlerNotFoundException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event (Resubmission)", exception);
                await SendUnsupportedResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (EventContextHandlerException exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to handle event (Resubmission)", exception);
                // Event handler threw (non-transient) exception.
                await SendErrorResponse(messageContext, exception, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
        }

        public override async Task HandleSkipRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfoWithMessageMetaData(logger, messageContext, "Handle (Skip)");
                AuthorizeManagerRequest(messageContext);
                await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
                await UnblockSession(messageContext, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, logger, cancellationToken);
                await SendSkipResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (SessionBlockedException)
            {
                // Session is not blocked by this, so the resubmitted event must have already been resolved.
                await SendSkipResponse(messageContext, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }

            LogInfoWithMessageMetaData(logger, messageContext, "Successfully processed (Skip)");
        }

        public override async Task HandleContinuationRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfoWithMessageMetaData(logger, messageContext, "Handle (Continuation)");

                AuthorizeContinuationRequest(messageContext);
                IMessageContext deferredMessageContext = await ReceiveNextDeferredAndVerifyEventId(messageContext, true, cancellationToken);
                await HandleEventRequest(deferredMessageContext, logger, cancellationToken);
                await ContinueWithAnyDeferredMessages(messageContext, logger, cancellationToken);
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (EventContextHandlerException)
            {
                await CompleteMessage(messageContext, cancellationToken);
            }
            catch (NextDeferredException)
            {
                // Either: 1) There is no next deferred event request,
                // or 2) EventId of continuation request does not match EventId of next deferred event request.
                // Either way, the requested continuation must have already been resolved.
                await CompleteMessage(messageContext, cancellationToken);
            }

            LogInfoWithMessageMetaData(logger, messageContext, "Successfully processed (Continuation)");
        }

        public override async Task HandleProcessDeferredRequest(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                LogInfoWithMessageMetaData(logger, messageContext, "Handle (ProcessDeferredRequest)");

                if (_deferredMessageProcessor == null)
                    throw new InvalidOperationException("DeferredMessageProcessor is not configured. Cannot process deferred messages.");

                if (string.IsNullOrEmpty(_topicName))
                    throw new InvalidOperationException("Topic name is not configured. Cannot process deferred messages.");

                // Process all deferred messages for this session
                await _deferredMessageProcessor.ProcessDeferredMessagesAsync(messageContext.SessionId, _topicName, cancellationToken);

                // Reset the deferred count since all messages have been republished
                await messageContext.ResetDeferredCount(cancellationToken);

                await CompleteMessage(messageContext, cancellationToken);

                LogInfoWithMessageMetaData(logger, messageContext, "Successfully processed (ProcessDeferredRequest)");
            }
            catch (Exception exception)
            {
                LogErrorWithMessageMetaData(logger, messageContext, "Failed to process deferred messages", exception);
                throw;
            }
        }

        private Task CompleteMessage(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.Complete(cancellationToken);

        private Task DeferMessage(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.Defer(cancellationToken);

        private async Task DeferMessageToSubscription(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            // Get the next deferral sequence number for ordering
            int deferralSequence = await messageContext.GetNextDeferralSequenceAndIncrement(cancellationToken);

            // Send message to deferred subscription
            await _responseService.SendToDeferredSubscription(messageContext, deferralSequence, cancellationToken);

            // Increment deferred count in session state
            await messageContext.IncrementDeferredCount(cancellationToken);

            // Complete the original message (it's now stored in the deferred subscription)
            await messageContext.Complete(cancellationToken);
        }

        private Task SendResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _responseService.SendResolutionResponse(messageContext, cancellationToken);

        private Task SendSkipResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _responseService.SendSkipResponse(messageContext, cancellationToken);

        private Task SendErrorResponse(IMessageContext messageContext, EventContextHandlerException exception, CancellationToken cancellationToken = default) =>
            _responseService.SendErrorResponse(messageContext, exception, cancellationToken);

        private Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default) =>
            _responseService.SendDeferralResponse(messageContext, exception, cancellationToken);

        private Task BlockSession(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.BlockSession(cancellationToken);

        private Task UnblockSession(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.UnblockSession(cancellationToken);

        private Task SendRetryResponse(IMessageContext messageContext, int messageDelayMinutes, CancellationToken cancellationToken = default) =>
            _responseService.SendRetryResponse(messageContext, messageDelayMinutes, cancellationToken);

        private Task SendUnsupportedResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            _responseService.SendUnsupportedResponse(messageContext, cancellationToken);

        private async Task<IMessageContext> ReceiveNextDeferredAndVerifyEventId(IMessageContext messageContext, bool removeFromQueue = false, CancellationToken cancellationToken = default)
        {
            IMessageContext nextDeferred;
            if (removeFromQueue)
            {
                nextDeferred = await messageContext.ReceiveNextDeferredWithPop(cancellationToken);
            }
            else
            {
                nextDeferred = await messageContext.ReceiveNextDeferred(cancellationToken);
            }

            if (!messageContext.EventId.Equals(nextDeferred?.EventId, StringComparison.OrdinalIgnoreCase))
                throw new NextDeferredException($"Unable to continue with {messageContext.EventId}, because it is not the next deferred event request in this session.");
            return nextDeferred;
        }

        private void AuthorizeManagerRequest(IMessageContext messageContext)
        {
            if (!messageContext.From.Equals(Constants.ManagerId, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Only {Constants.ManagerId} is authorized to send {messageContext.MessageType} messages.");
        }

        private void AuthorizeContinuationRequest(IMessageContext messageContext)
        {
            if (!messageContext.From.Equals(Constants.ContinuationId, StringComparison.OrdinalIgnoreCase) &&
                !messageContext.From.Equals(Constants.ManagerId, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"{messageContext.From} is not authorized to send {MessageType.ContinuationRequest} messages to this messaging entity.");
        }

        private async Task VerifySessionIsBlockedByThis(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            if (!await messageContext.IsSessionBlockedByThis(cancellationToken))
            {
                var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}");
            }
        }

        private async Task VerifySessionIsNotBlocked(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
            if (!string.IsNullOrEmpty(blockedBy))
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}");
        }

        private async Task HandleEventContent(IMessageContext context, ILogger logger, CancellationToken cancellationToken = default)
        {
            try
            {
                await _eventContextHandler.Handle(context, logger, cancellationToken);
            }
            catch (TransientException)
            {
                throw;
            }
            catch (EventHandlerNotFoundException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new EventContextHandlerException(exception)
                {
                    Source = exception.Source
                };
            }
        }

        private async Task ContinueWithAnyDeferredMessages(IMessageContext messageContext, ILogger logger, CancellationToken cancellationToken = default)
        {
            // First, check for legacy deferred messages in session state
            var next = await messageContext.ReceiveNextDeferred(cancellationToken);
            if (next != null)
            {
                await _responseService.SendContinuationRequestToSelf(next, cancellationToken);
                LogInfoWithMessageMetaData(logger, messageContext, "Send ContinuationRequest (legacy)");
                return;
            }

            // Then, check for new subscription-based deferred messages
            var deferredCount = await messageContext.GetDeferredCount(cancellationToken);
            if (deferredCount > 0)
            {
                await _responseService.SendProcessDeferredRequest(messageContext, cancellationToken);
                LogInfoWithMessageMetaData(logger, messageContext, $"Send ProcessDeferredRequest ({deferredCount} deferred messages)");
            }
        }

        private async Task CheckForRetry(IMessageContext messageContext, EventContextHandlerException exception, CancellationToken cancellationToken = default)
        {
            var retryDefinition = RetryDefinitions.GetRetryDefinition(messageContext.MessageContent.EventContent.EventTypeId,
                $"{exception?.InnerException} {exception}", messageContext.To);
            if (retryDefinition != null && messageContext.RetryCount != null && messageContext.RetryCount < retryDefinition.RetryCount)
            {
                await SendRetryResponse(messageContext, retryDefinition.RetryDelay, cancellationToken);
            }
        }

        private void LogInfoWithMessageMetaData(ILogger logger, IMessageContext messageContext, string prefixMessage)
        {
            var logMetaData = "EventTypeId: {EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}";

            logger.Information($"{prefixMessage} {logMetaData}",
                messageContext.MessageContent?.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);
        }

        private void LogErrorWithMessageMetaData(ILogger logger, IMessageContext messageContext, string prefixMessage, Exception exception)
        {
            var logMetaData = "EventTypeId: {EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}";

            logger.Error(exception, $"{prefixMessage} {logMetaData}",
                messageContext.MessageContent?.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);
        }
    }
}
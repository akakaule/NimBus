using System.Runtime.CompilerServices;

// Type-forwarders for types relocated to NimBus.Transport.Abstractions during the
// Phase 6.1 transport-provider promotion (issue #18). The namespace is preserved
// (NimBus.Core.Messages and NimBus.Core.Messages.Exceptions) so existing
// `using NimBus.Core.Messages;` directives in consumer code keep resolving.
//
// Wire-model batch — moved alongside the transport interfaces because the
// interfaces reference these types in their signatures.
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.IMessage))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.MessageType))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.MessageContent))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.EventContent))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.ErrorContent))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.Exceptions.TransientException))]

// Transport-interface batch (Pass 1 of task #18). Only the dependency-clean
// interfaces ship in this pass:
//   - ISender depends solely on IMessage, which moved with the wire-model batch
//   - IDeferredMessageProcessor depends only on BCL types
// IMessageContext, IReceivedMessage, and IMessageHandler (which references
// IMessageContext) are deferred to Pass 2. They land once the in-flight
// extraction of ScheduleRedelivery + ThrottleRetryCount from IMessageContext
// (task #16 follow-up C) is complete, so the transport surface promotes in its
// final post-disentanglement shape rather than racing further mutations.
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.ISender))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.IDeferredMessageProcessor))]

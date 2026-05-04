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

// Transport-interface batch — Pass 1 (dependency-clean):
//   - ISender depends solely on IMessage, which moved with the wire-model batch
//   - IDeferredMessageProcessor depends only on BCL types
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.ISender))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.IDeferredMessageProcessor))]

// Transport-interface batch — Pass 2 (post-disentanglement). IMessageContext is
// in its final shape after task #16 follow-up C extracted ScheduleRedelivery
// and ThrottleRetryCount, so these three interfaces can move without racing
// further mutations. IMessageHandler references IMessageContext, so the three
// must promote together.
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.IReceivedMessage))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.IMessageContext))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Messages.IMessageHandler))]

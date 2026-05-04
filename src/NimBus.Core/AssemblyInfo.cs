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

using System.Runtime.CompilerServices;

// Forward event types to NimBus.Abstractions
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.IEvent))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.Event))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.EventValidationResult))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.IEventType))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.EventType))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.EventType<>))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.IProperty))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Events.Property))]

// Forward endpoint types to NimBus.Abstractions
[assembly: TypeForwardedTo(typeof(NimBus.Core.Endpoints.IEndpoint))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Endpoints.Endpoint))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Endpoints.ISystem))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Endpoints.IRoleAssignment))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Endpoints.RoleAssignment))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Endpoints.Environment))]

// Forward platform types to NimBus.Abstractions
[assembly: TypeForwardedTo(typeof(NimBus.Core.IPlatform))]
[assembly: TypeForwardedTo(typeof(NimBus.Core.Platform))]

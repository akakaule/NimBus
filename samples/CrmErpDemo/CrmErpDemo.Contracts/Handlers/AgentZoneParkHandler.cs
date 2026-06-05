using CrmErpDemo.Contracts.Events;
using NimBus.SDK.EventHandlers;

namespace CrmErpDemo.Contracts.Handlers;

/// <summary>
/// Spec 022 — typed park handler for the Agent Zone endpoint.
/// Receives a <see cref="CrmContactCreated"/> event and immediately parks it as
/// Pending+Handoff so an external agent can pull and settle it via the REST API.
/// </summary>
public sealed class AgentZoneParkHandler : IEventHandler<CrmContactCreated>
{
    /// <inheritdoc />
    public Task Handle(CrmContactCreated @event, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        context.MarkPendingHandoff("Awaiting agent pickup");
        return Task.CompletedTask;
    }
}

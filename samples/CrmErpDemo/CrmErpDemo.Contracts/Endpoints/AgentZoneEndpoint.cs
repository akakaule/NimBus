using CrmErpDemo.Contracts.Events;
using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts.Endpoints;

/// <summary>Spec 022 Agent Zone: pre-provisioned endpoint carrying dynamically-typed agent events.</summary>
public sealed class AgentZoneEndpoint : Endpoint
{
    public AgentZoneEndpoint()
    {
        Consumes<CrmContactCreated>();
    }

    public override string Description =>
        "Spec 022 Agent Zone endpoint. Consumes CrmContactCreated and parks agent-targeted events as Pending+Handoff for external agents to pull and settle via REST.";
}

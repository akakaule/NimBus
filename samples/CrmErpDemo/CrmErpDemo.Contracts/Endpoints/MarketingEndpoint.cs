using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts.Endpoints;

/// <summary>Spec 023 Marketing source endpoint. Publishes dynamically-typed marketing.lead.created.v1 events.</summary>
public sealed class MarketingEndpoint : Endpoint
{
    public override ISystem System => new MarketingSystem();

    public override string Description =>
        "Spec 023 Marketing source endpoint. Publishes marketing.lead.created.v1 as a dynamically-typed event routed to the Mapping Zone for AI-authored translation.";
}

internal sealed class MarketingSystem : ISystem
{
    public string SystemId => "Marketing";
}

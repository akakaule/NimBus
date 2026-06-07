using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts.Endpoints;

/// <summary>Spec 023 Mapping Zone: pre-provisioned endpoint carrying dynamically-typed events awaiting AI-authored mapping translation.</summary>
public sealed class MappingZoneEndpoint : Endpoint
{
    public override string Description =>
        "Spec 023 Mapping Zone endpoint. Receives marketing.lead.created.v1 events from the Marketing source and routes erp.customer.upsert.v1 output to the DataPlatform consumer after AI mapping execution.";
}

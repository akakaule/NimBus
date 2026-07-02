using CrmErpDemo.Contracts.Endpoints;
using NimBus.Core;
using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts;

public class CrmErpPlatformConfiguration : Platform
{
    // Dynamic event forwards: events with a string EventTypeId but no compiled IEvent class.
    // Consumed by both EmulatorTopologyConfigBuilder and ServiceBusTopologyProvisioner (spec 022 D5).
    private static readonly IReadOnlyList<DynamicForward> _dynamicForwards =
    [
        // Spec 022: enriched CRM contacts flow from Agent Zone to DataPlatform.
        new DynamicForward("AgentZoneEndpoint", "crm.contact.enriched.v1", "DataPlatformEndpoint"),
    ];

    public override IReadOnlyList<DynamicForward> DynamicForwards => _dynamicForwards;

    public CrmErpPlatformConfiguration()
    {
        AddEndpoint(new CrmEndpoint());
        AddEndpoint(new ErpEndpoint());
        AddEndpoint(new DataPlatformEndpoint());
        AddEndpoint(new AgentZoneEndpoint());
    }
}

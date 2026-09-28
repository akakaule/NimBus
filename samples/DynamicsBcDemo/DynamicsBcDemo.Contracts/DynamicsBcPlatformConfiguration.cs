using DynamicsBcDemo.Contracts.Endpoints;
using NimBus.Core;

namespace DynamicsBcDemo.Contracts;

/// <summary>
/// The platform catalog for the Dynamics 365 Sales ↔ Business Central demo: two endpoints,
/// one per system. Loaded by the provisioner (topology) and by nimbus-ops
/// (NimBus__PlatformType / NimBus__PlatformAssembly).
/// </summary>
public class DynamicsBcPlatformConfiguration : Platform
{
    public DynamicsBcPlatformConfiguration()
    {
        AddEndpoint(new D365SalesEndpoint());
        AddEndpoint(new BusinessCentralEndpoint());
    }
}

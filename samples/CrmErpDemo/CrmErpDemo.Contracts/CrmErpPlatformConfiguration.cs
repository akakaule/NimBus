using CrmErpDemo.Contracts.Endpoints;
using NimBus.Core;

namespace CrmErpDemo.Contracts;

public class CrmErpPlatformConfiguration : Platform
{
    public CrmErpPlatformConfiguration()
    {
        AddEndpoint(new CrmEndpoint());
        AddEndpoint(new ErpEndpoint());
        AddEndpoint(new DataPlatformEndpoint());
    }
}

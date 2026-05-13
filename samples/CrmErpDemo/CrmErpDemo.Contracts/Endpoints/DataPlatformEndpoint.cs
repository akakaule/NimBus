using CrmErpDemo.Contracts.Events;
using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts.Endpoints;

public class DataPlatformEndpoint : Endpoint
{
    public DataPlatformEndpoint()
    {
        Consumes<ErpCustomerCreated>();
    }

    public override ISystem System => new DataPlatformSystem();

    public override string Description =>
        "Data-platform sink endpoint. Subscribes to ErpCustomerCreated for downstream analytics; publishes nothing.";
}

internal sealed class DataPlatformSystem : ISystem
{
    public string SystemId => "DataPlatform";
}

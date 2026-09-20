using NimBus.Core.Endpoints;

namespace NimBus.Adapters.Dataverse.Contracts;

/// <summary>Add this endpoint to the customer's Platform catalog before enabling ingress.</summary>
public sealed class DataverseEndpoint : Endpoint
{
    /// <summary>Declare the supported version-one record event family.</summary>
    public DataverseEndpoint()
    {
        Produces<DataverseRecordCreatedV1>();
        Produces<DataverseRecordUpdatedV1>();
        Produces<DataverseRecordDeletedV1>();
    }

    /// <inheritdoc/>
    public override string Description => "Dataverse record changes received by the optional Functions adapter.";
}

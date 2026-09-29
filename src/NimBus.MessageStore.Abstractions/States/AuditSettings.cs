using System.Collections.Generic;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>
/// Platform-wide choice of which operator actions the WebApp records in the audit log.
/// A single record: one row on SQL Server, one document with a fixed id on Cosmos. A store
/// that has never been written returns these defaults — every audit type recorded.
/// </summary>
public class AuditSettings
{
    /// <summary>Fixed id of the singleton record.</summary>
    public const string SingletonId = "AuditSettings";

    /// <summary>Record id. Always <see cref="SingletonId"/>.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = SingletonId;

    /// <summary>
    /// <see cref="MessageAuditType"/> names that are not recorded. Stored by name rather
    /// than value so the record survives enum reordering across providers.
    /// </summary>
    public List<string> DisabledAuditTypes { get; set; } = new();
}

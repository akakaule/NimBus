using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;

namespace NimBus.Adapters.Dataverse.Contracts;

/// <summary>A selected set of attributes from a Dataverse operation, never an implicit full snapshot.</summary>
[SessionKey(nameof(SessionId))]
public abstract class DataverseRecordEvent : Event
{
    /// <summary>Dataverse organization identity.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Registered step identity from the execution context.</summary>
    public Guid RegistrationId { get; set; }

    /// <summary>Source asynchronous operation identity; stability requires tenant verification.</summary>
    public Guid OperationId { get; set; }

    /// <summary>Dataverse table logical name.</summary>
    [Required]
    public string Table { get; set; } = string.Empty;

    /// <summary>Record identity.</summary>
    public Guid RecordId { get; set; }

    /// <summary>Original correlation, distinct from the event identity.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>Configured organization/table/record ordering key.</summary>
    [Required]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Selected target attributes. Missing keys mean absent, not null.</summary>
    public JObject Attributes { get; set; } = new();

    /// <summary>Selected pre-image attributes, null when no image was requested.</summary>
    public JObject? Before { get; set; }

    /// <summary>Selected post-image attributes, null when no image was requested.</summary>
    public JObject? After { get; set; }
}

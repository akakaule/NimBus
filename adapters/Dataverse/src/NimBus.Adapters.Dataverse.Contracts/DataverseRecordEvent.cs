using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

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

    /// <summary>
    /// Selected target attributes. Missing keys mean absent, not null. Sensitive as a whole:
    /// the projected columns are configuration-driven, so no per-column annotation can exist
    /// and the masker must not treat unclassified business data as safe to reveal.
    /// </summary>
    [Sensitive]
    public JObject Attributes { get; set; } = new();

    /// <summary>Selected pre-image attributes, null when no image was requested. Sensitive; see <see cref="Attributes"/>.</summary>
    [Sensitive]
    public JObject? Before { get; set; }

    /// <summary>Selected post-image attributes, null when no image was requested. Sensitive; see <see cref="Attributes"/>.</summary>
    [Sensitive]
    public JObject? After { get; set; }
}

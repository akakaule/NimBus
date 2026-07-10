using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.Events;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Crm.Adapter.Handlers;

// Consumes raw CloudEvents 1.0 messages published by the external PartnerPortal
// (which has no NimBus dependency) to the PartnerInbound topic. The subscriber's
// AutoDetect mode maps the CloudEvents `type` attribute's last dot-segment
// ("com.partnerportal.crm.PartnerLeadSubmitted") to this event's EventTypeId.
public sealed class PartnerLeadSubmittedHandler(ICrmApiClient crm, ILogger<PartnerLeadSubmittedHandler> logger)
    : IEventHandler<PartnerLeadSubmitted>
{
    public async Task Handle(PartnerLeadSubmitted message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var cloudEvent = context.GetCloudEvent();
        logger.LogInformation(
            "Creating CRM contact from partner lead {LeadId} (CloudEvent id={CloudEventId}, source={CloudEventSource}, type={CloudEventType})",
            message.LeadId,
            cloudEvent?.Id,
            cloudEvent?.Source,
            cloudEvent?.Type);

        // Partner leads are not linked to an ERP customer; the contact is created
        // account-less with Origin "Partner" so the CRM UI shows where it came from.
        await crm.UpsertContactAsync(
            message.LeadId,
            new ContactPayload(null, message.FirstName, message.LastName, message.Email, message.Phone, Origin: "Partner"),
            cancellationToken);
    }
}

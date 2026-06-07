using NimBus.Core.Messages;
using NimBus.SDK;
using Newtonsoft.Json;

namespace Marketing.Api.Endpoints;

public static class LeadEndpoints
{
    // The dynamically-typed event type id published by this source.
    private const string LeadEventTypeId = "marketing.lead.created.v1";

    public static void MapLeadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leads");

        // POST /api/leads — publish a marketing.lead.created.v1 event.
        // Body: { "leadId": "...", "firstName": "...", "lastName": "...", "company": "...", "email": "..." }
        // Mirrors the classless dynamic-message publish pattern from the agent REST API (spec 022).
        group.MapPost("/", async (LeadInput input, IPublisherClient publisher) =>
        {
            if (string.IsNullOrWhiteSpace(input.LeadId))
                input = input with { LeadId = Guid.NewGuid().ToString() };

            var payloadJson = JsonConvert.SerializeObject(new
            {
                leadId = input.LeadId,
                firstName = input.FirstName,
                lastName = input.LastName,
                company = input.Company,
                email = input.Email,
            });

            // Publish as a classless dynamic message (string EventTypeId, no compiled IEvent).
            // Mirrors how AgentImplementation.PostAgentPublishAsync builds the CoreMessage.
            var message = new Message
            {
                To = LeadEventTypeId,
                EventTypeId = LeadEventTypeId,
                SessionId = input.LeadId,
                CorrelationId = Guid.NewGuid().ToString(),
                MessageId = Guid.NewGuid().ToString(),
                RetryCount = 0,
                MessageType = MessageType.EventRequest,
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = LeadEventTypeId,
                        EventJson = payloadJson,
                    },
                },
            };

            await publisher.Publish(message);
            return Results.Ok(new { message = "Lead published.", leadId = input.LeadId });
        });

        // GET /api/leads/ping — health-check that the Marketing API is up.
        group.MapGet("/ping", () => Results.Ok(new { status = "Marketing API online." }));
    }
}

// Input shape for the marketing lead event.
public record LeadInput(
    string? LeadId,
    string FirstName,
    string LastName,
    string Company,
    string Email);

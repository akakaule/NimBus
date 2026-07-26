using CrmErpDemo.Contracts.Commands;
using CrmErpDemo.Contracts.Dtos;
using CrmErpDemo.Contracts.Events;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK;

namespace Crm.Api.Endpoints;

/// <summary>
/// CRM-side entry points for the request/reply and command showcases:
/// a synchronous ERP credit check (PublisherClient.Request over the
/// CrmEndpoint-reply subscription) and the fire-and-forget
/// PlaceCustomerOnCreditHold command.
/// </summary>
public static class ErpIntegrationEndpoints
{
    private static readonly TimeSpan CreditCheckTimeout = TimeSpan.FromSeconds(10);

    public static void MapErpIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts");

        // Request/reply showcase: ask ERP synchronously for the account's credit
        // standing. Blocks (up to the timeout) while the ERP adapter handles the
        // request and replies on CrmEndpoint-reply.
        group.MapPost("/{id:guid}/credit-check", async (Guid id, CrmDbContext db, IPublisherClient publisher, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("Crm.Api.ErpIntegrationEndpoints");
            if (await db.Accounts.FindAsync(id) is null)
                return Results.NotFound();

            try
            {
                var result = await publisher.Request<ErpCreditCheckRequested, ErpCreditCheckResult>(
                    new ErpCreditCheckRequested
                    {
                        AccountId = id,
                        RequestedBy = "crm-web",
                        RequestedAt = DateTimeOffset.UtcNow,
                    },
                    CreditCheckTimeout);
                return Results.Ok(result);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Credit check for account {AccountId} timed out after {Timeout}s", id, CreditCheckTimeout.TotalSeconds);
                return Results.Json(
                    new { error = $"ERP did not answer within {CreditCheckTimeout.TotalSeconds:0} seconds. Is the ERP adapter running (or in maintenance mode)?" },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (RequestReplyException ex)
            {
                logger.LogWarning("Credit check for account {AccountId} failed on the ERP side: {ErrorType}: {ErrorText}", id, ex.ErrorType, ex.ErrorText);
                return Results.Json(
                    new { error = $"ERP failed to run the credit check: {ex.ErrorText}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (NotSupportedException)
            {
                return Results.Json(
                    new { error = "Request/reply requires Service Bus; the API is running without it." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Command showcase: fire-and-forget imperative message with exactly one
        // consumer (ErpEndpoint). The hold becomes observable via the ERP UI and
        // a subsequent credit check returning OnHold.
        group.MapPost("/{id:guid}/credit-hold", async (Guid id, CreditHoldBody? body, CrmDbContext db, IPublisherClient publisher, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("Crm.Api.ErpIntegrationEndpoints");
            if (await db.Accounts.FindAsync(id) is null)
                return Results.NotFound();

            var command = new PlaceCustomerOnCreditHold
            {
                AccountId = id,
                Reason = string.IsNullOrWhiteSpace(body?.Reason) ? "Requested from CRM" : body!.Reason,
                RequestedAt = DateTimeOffset.UtcNow,
            };
            logger.LogInformation("Sending PlaceCustomerOnCreditHold for account {AccountId}", id);
            await publisher.Publish(command);
            return Results.Accepted($"/api/accounts/{id}", new { accepted = true });
        });
    }
}

public record CreditHoldBody(string? Reason);

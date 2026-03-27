using NimBus.Events.Orders;
using NimBus.SDK;
using NimBus.SDK.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddNimBusPublisher("StorefrontEndpoint");

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapPost("/publish/order", async (IPublisherClient publisher) =>
{
    var order = new OrderPlaced
    {
        OrderId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        CurrencyCode = "EUR",
        TotalAmount = 42.50m,
        SalesChannel = "aspire-sample"
    };

    await publisher.Publish(order);

    return Results.Ok(new { order.OrderId, Status = "Published" });
});

app.MapPost("/publish/order-failed", async (IPublisherClient publisher) =>
{
    var order = new OrderPlaced
    {
        OrderId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        CurrencyCode = "EUR",
        TotalAmount = 42.50m,
        SalesChannel = "aspire-sample",
        SimulateFailure = true
    };

    await publisher.Publish(order);

    return Results.Ok(new { order.OrderId, Status = "Published (will fail)" });
});

app.Run();

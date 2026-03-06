using System.Threading.Tasks;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace NimBus.WebApp.Controllers;

public class DevImplementation : IDevApiController
{
    private readonly SeedDataService _seedDataService;
    private readonly IWebHostEnvironment _env;

    public DevImplementation(SeedDataService seedDataService, IWebHostEnvironment env)
    {
        _seedDataService = seedDataService;
        _env = env;
    }

    public async Task<ActionResult<SeedResult>> PostDevSeedAsync()
    {
        if (!_env.IsDevelopment())
            return new NotFoundResult();

        var result = await _seedDataService.SeedAsync();
        return result;
    }

    public async Task<IActionResult> DeleteDevSeedAsync()
    {
        if (!_env.IsDevelopment())
            return new NotFoundResult();

        await _seedDataService.ClearSeedDataAsync();
        return new OkObjectResult(new { message = "Sample data cleared" });
    }

    public async Task<IActionResult> PostDevCreateEventAsync(CreateEventRequest body, string endpointId)
    {
        if (!_env.IsDevelopment())
            return new NotFoundResult();

        await _seedDataService.CreateEventAsync(
            endpointId,
            body.EventTypeId ?? "CustomerChanged",
            body.SessionId,
            body.Status.ToString(),
            body.MessageContent);

        return new OkObjectResult(new { message = "Event created", endpointId });
    }

    public async Task<IActionResult> PostDevSendMessageAsync(SendMessageRequest body)
    {
        if (!_env.IsDevelopment())
            return new NotFoundResult();

        await _seedDataService.CreateMessageAsync(
            body.EndpointId,
            body.EventTypeId,
            body.SessionId,
            body.MessageContent);

        return new OkObjectResult(new { message = "Message sent", endpointId = body.EndpointId });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NimBus.WebApp.Hubs
{
    // [Authorize] closes the anonymous-WebSocket path. The hub broadcasts
    // endpoint state (counts of pending / failed / deferred / handoff) to
    // every authenticated operator; without this attribute anonymous clients
    // can negotiate and receive the same broadcasts. See spec 010.
    [Authorize]
    public class GridEventsHub : Hub
    {
        public GridEventsHub()
        {
        }
    }
}

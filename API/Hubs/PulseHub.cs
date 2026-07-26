using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace KindleKeep.Api.API.Hubs;

public sealed class PulseHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, userId);
        }

        await base.OnConnectedAsync();
    }

    public async Task SubscribeToMonitor(string monitorId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, monitorId);
    }

    public async Task UnsubscribeFromMonitor(string monitorId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, monitorId);
    }
}
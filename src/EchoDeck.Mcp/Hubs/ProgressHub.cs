using Microsoft.AspNetCore.SignalR;

namespace EchoDeck.Mcp.Hubs;

public class ProgressHub : Hub
{
    public async Task JoinJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
    }

    public async Task LeaveJob(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }
}

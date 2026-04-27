using Microsoft.AspNetCore.SignalR;

namespace AgentStationHub.Hubs;

public class DeploymentHub : Hub
{
    public Task Join(string sessionId) => Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    public Task Leave(string sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
}

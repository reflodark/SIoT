using Microsoft.AspNetCore.SignalR;
using MQTTnet.Server;

public class SIoTHub : Hub<IHub>
{
    public async Task SendToAll(object data)
    => await Clients.All.ReceiveMessage(data);

    public async Task SendToOthers(object data)
    => await Clients.Others.ReceiveMessage(data);

    public async Task SendToGroup(string group, object data)
    => await Clients.Group(group).ReceiveMessage(data);   
}
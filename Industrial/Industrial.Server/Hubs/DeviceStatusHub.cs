using Microsoft.AspNetCore.SignalR;

namespace Industrial.Server.Hubs;

// 定义强类型客户端接口，规范前端能接收的方法
public interface IDeviceStatusClient
{
    Task ReceiveTemperatureWarning(string deviceId, double temperature);
    Task UpdateMachineState(int stateCode, string description);
}

public class DeviceStatusHub : Hub<IDeviceStatusClient>
{
    // 客户端连接时的处理逻辑
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "ValidClients");
        await base.OnConnectedAsync();
    }

    // WPF 客户端可以主动调用的服务端方法 (即 Client -> Server 推送消息)
    public async Task SendClientLogToServer(string message)
    {
        // 收到 WPF 客户端的推流后，我们仅仅在服务端控制台打出来，不再反向给其他客户端。
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[SignalR Client Msg] '{Context.ConnectionId}' 报告: {message}");
        Console.ResetColor();

        // 此处不需要再调用 Clients.* 去反推给客户端。
        await Task.CompletedTask;
    }
}

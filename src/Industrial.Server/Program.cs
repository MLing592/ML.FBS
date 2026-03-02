using Industrial.Server.Hubs;
using Industrial.Server.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// 注册 gRPC 与 SignalR
builder.Services.AddGrpc();
builder.Services.AddSignalR(); 

// 注册 WebAPI (用以提供让外部系统或 WPF 去触发报警的 HTTP 接口)
builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();

// 映射终结点
app.MapGrpcService<DeviceControlService>();
app.MapHub<DeviceStatusHub>("/hubs/deviceStatus");

// 一个供外部触发 SignalR 报警的路由
app.MapPost("/api/alarm/trigger", async (Microsoft.AspNetCore.SignalR.IHubContext<DeviceStatusHub> hubContext) =>
{
    // 向前连接的客户端群体广播警告
    await hubContext.Clients.All.SendAsync("ReceiveTemperatureWarning", "REMOTE-TRIGGER-001", 99.9);
    return Results.Ok(new { message = "Alarm triggered globally" });
});

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();

using Industrial.Server.Hubs;
using Industrial.Server.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// 注册 gRPC 与 SignalR
builder.Services.AddGrpc();
builder.Services.AddSignalR(); 

// 注册 WebAPI (用以提供让外部系统或 WPF 去触发报警的 HTTP 接口)
builder.Services.AddControllers();

// 注册每3秒自动推送仿真数据的后台服务
builder.Services.AddHostedService<DataSimulationWorker>();

// 配置 CORS 以支持 Vue 客户端的 SignalR 和 WebAPI 请求
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVueClient", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
var app = builder.Build();

app.UseRouting();

app.UseCors("AllowVueClient");

// 映射终结点
app.MapGrpcService<DeviceControlService>();
app.MapHub<DeviceStatusHub>("/hubs/deviceStatus");

// 一个供外部触发主动拉取当前温度的路由
app.MapPost("/api/temperature/push_current", async (Microsoft.AspNetCore.SignalR.IHubContext<DeviceStatusHub, IDeviceStatusClient> hubContext) =>
{
    // 向前端主动推一次最新正常温度
    double currentTemp = 40.0 + new Random().NextDouble() * 15.0; 
    await hubContext.Clients.Group("ValidClients").ReceiveCurrentTemperature(Math.Round(currentTemp, 1));
    return Results.Ok(new { message = "Current temperature pushed" });
});

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();

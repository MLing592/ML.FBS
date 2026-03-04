using System.Text.Json.Serialization;
using Industrial.Server.Hubs;
using Industrial.Server.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// builder.WebHost.ConfigureKestrel(options =>
// {
//     options.ListenLocalhost(7212, listenOptions =>
//     {
//         listenOptions.UseHttps();
//     });
//     options.ListenLocalhost(5268);
// });

// 注册 gRPC 与 SignalR
builder.Services.AddGrpc();

// AOT 下必须显式配置 JSON TypeInfoResolver，否则反射序列化会被禁用
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.TypeInfoResolver =
            new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
    });

// 注册 WebAPI (用以提供让外部系统或 WPF 去触发报警的 HTTP 接口)
builder.Services.AddControllers();

// 注册每3秒自动推送仿真数据的后台服务
builder.Services.AddHostedService<DataSimulationWorker>();

// 配置 HTTP JSON 使用 AOT Source Generator Context
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

// 配置 CORS 以支持 Vue 客户端的 SignalR 和 WebAPI 请求
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVueClient", policy =>
    {
        policy.WithOrigins("http://localhost:5175", "http://127.0.0.1:5175")
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

app.MapPost("/api/temperature/push_current", async (Microsoft.AspNetCore.SignalR.IHubContext<DeviceStatusHub> hubContext) =>
{
    // 向前端主动推一次最新正常温度
    double currentTemp = 40.0 + new Random().NextDouble() * 15.0;
    await hubContext.Clients.Group("ValidClients").SendAsync("ReceiveCurrentTemperature", Math.Round(currentTemp, 1));
    // AOT 兼容：返回已在 AppJsonContext 注册的具名类型
    return Results.Ok(new PushResult("Current temperature pushed"));
});

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();

// AOT 兼容的响应类型 (Minimal API 返回用)
internal record PushResult(string Message);

// AOT JSON 序列化上下文：静态注册所有需要序列化的类型
[JsonSerializable(typeof(PushResult))]
internal partial class AppJsonContext : JsonSerializerContext { }

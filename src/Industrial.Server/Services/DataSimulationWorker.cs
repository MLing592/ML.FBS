using Industrial.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Industrial.Server.Services;

public class DataSimulationWorker : BackgroundService
{
    private readonly IHubContext<DeviceStatusHub> _hubContext;

    public DataSimulationWorker(IHubContext<DeviceStatusHub> hubContext)
    {
        _hubContext = hubContext;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 拆分为3个互相独立不干涉的任务循环，对应不同的推送频率
        _ = Task.Run(() => MachineStateLoop(stoppingToken), stoppingToken);
        _ = Task.Run(() => TemperatureWarningLoop(stoppingToken), stoppingToken);
        _ = Task.Run(() => CurrentTemperatureLoop(stoppingToken), stoppingToken);
        
        return Task.CompletedTask;
    }

    private async Task MachineStateLoop(CancellationToken token)
    {
        var random = new Random();
        var states = new[] {
            "底层服务启动初始化完成", 
            "主轴正在加速至目标转速", 
            "系统切换至G代码轨迹切割模式", 
            "冷却液泵浦工作，热量耗散中", 
            "工件自动翻转工序进行中", 
            "正在等待传送带补充料件", 
            "单批次CNC加工完美收尾",
            "激光测距动态补偿运作中"
        };

        while (!token.IsCancellationRequested)
        {
            int stateCode = random.Next(100, 600);
            string desc = states[random.Next(states.Length)];
            await _hubContext.Clients.Group("ValidClients").SendAsync("UpdateMachineState", stateCode, desc);
            
            // 区域1: 3秒发一次机床状态流
            await Task.Delay(3000, token);
        }
    }

    private async Task TemperatureWarningLoop(CancellationToken token)
    {
        var random = new Random();
        while (!token.IsCancellationRequested)
        {
            double warningTemp = 85.0 + random.NextDouble() * 35.0; 
            await _hubContext.Clients.Group("ValidClients").SendAsync("ReceiveTemperatureWarning", $"DEV-{random.Next(10, 99)}A", Math.Round(warningTemp, 1));
            
            // 区域2: 4秒打一次温度预警流 (将会被绘制为曲线)
            await Task.Delay(4000, token);
        }
    }

    private async Task CurrentTemperatureLoop(CancellationToken token)
    {
        var random = new Random();
        while (!token.IsCancellationRequested)
        {
            double currentTemp = 40.0 + random.NextDouble() * 15.0; 
            await _hubContext.Clients.Group("ValidClients").SendAsync("ReceiveCurrentTemperature", Math.Round(currentTemp, 1));
            
            // 区域3: 5秒自动更新右上角的“当前实时温度”
            await Task.Delay(5000, token);
        }
    }
}

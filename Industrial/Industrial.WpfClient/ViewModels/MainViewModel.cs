using Grpc.Net.Client;
using Industrial.Shared.Protos;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Grpc.Core;
using OxyPlot;
using OxyPlot.Series;
using OxyPlot.Axes;
using System.Threading;
using System.Windows.Input;

namespace Industrial.WpfClient.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly GrpcChannel _grpcChannel;
        private readonly DeviceControl.DeviceControlClient _grpcClient;
        private readonly HubConnection _signalrConnection;

        private string _statusText = "Disconnected";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> GrpcMessages { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> SignalRMessages { get; } = new ObservableCollection<string>();

        public ICommand StartCommand { get; }
        public ICommand StreamDataCommand { get; }
        public ICommand UploadBatchCommand { get; }
        public ICommand LiveTeleoperationCommand { get; }
        public ICommand TriggerAlarmCommand { get; }
        public ICommand SendLogToServerCommand { get; }
        public ICommand DownloadSvgCommand { get; }

        public PlotModel PlotModel { get; private set; }
        private LineSeries _vibrationSeries;
        
        public PlotModel TrajectoryPlotModel { get; private set; }
        private ScatterSeries _trajectorySeries;

        private CancellationTokenSource? _streamCts;
        private CancellationTokenSource? _teleopCts; // 仅用于清理
        private CancellationTokenSource? _svgCts;
        private bool _isTeleopActive = false;

        private double _actualVelocity;
        public double ActualVelocity
        {
            get => _actualVelocity;
            set { _actualVelocity = value; OnPropertyChanged(); }
        }

        private double _actualAngle;
        public double ActualAngle
        {
            get => _actualAngle;
            set { _actualAngle = value; OnPropertyChanged(); }
        }

        private string _statusWarning = "None";
        public string StatusWarning
        {
            get => _statusWarning;
            set { 
                _statusWarning = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(WarningColor));
            }
        }

        public string WarningColor => _statusWarning == "Normal" || _statusWarning == "None" ? "LightGreen" : "LightCoral";

        public MainViewModel()
        {
            StartCommand = new RelayCommand(async _ => await SendStartCommandAsync());
            StreamDataCommand = new RelayCommand(async _ => await StartStreamingDataAsync());
            UploadBatchCommand = new RelayCommand(async _ => await UploadBatchAsync());
            LiveTeleoperationCommand = new RelayCommand(async _ => await StartLiveTeleoperationAsync());
            TriggerAlarmCommand = new RelayCommand(async _ => await TriggerRemoteAlarmAsync());
            SendLogToServerCommand = new RelayCommand(async _ => await SendLogToServerAsync());
            DownloadSvgCommand = new RelayCommand(async _ => await DownloadSvgAsync());

            // --- OxyPlot 图表 1: 震动高频流 ---
            PlotModel = new PlotModel { Title = "Vibration Real-time Data", TextColor = OxyColors.White };
            PlotModel.Background = OxyColor.FromRgb(30, 30, 30);
            
            _vibrationSeries = new LineSeries 
            { 
                Title = "Vib-X Sensor", 
                Color = OxyColors.Cyan, 
                StrokeThickness = 2 
            };
            PlotModel.Series.Add(_vibrationSeries);

            // --- OxyPlot 图表 2: 上传轨迹点 ---
            TrajectoryPlotModel = new PlotModel { Title = "CNC Upload Path", TextColor = OxyColors.White };
            TrajectoryPlotModel.Background = OxyColor.FromRgb(30, 30, 30);
            
            _trajectorySeries = new ScatterSeries 
            { 
                Title = "Toolpath Nodes", 
                MarkerType = MarkerType.Circle,
                MarkerFill = OxyColors.HotPink,
                MarkerSize = 3
            };
            TrajectoryPlotModel.Series.Add(_trajectorySeries);

            // --- 核心通信机制初始化 ---
            
            // 为了避免本地开发时没有安装/信任 SSL 证书导致的异常，我们忽略证书校验
            var handler = new System.Net.Http.HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            // 1. 初始化 gRPC 通道
            var grpcOptions = new GrpcChannelOptions { HttpHandler = handler };
            _grpcChannel = GrpcChannel.ForAddress("https://localhost:7212", grpcOptions); 
            _grpcClient = new DeviceControl.DeviceControlClient(_grpcChannel);

            // 2. 初始化 SignalR 连接
            _signalrConnection = new HubConnectionBuilder()
                .WithUrl("https://localhost:7212/hubs/deviceStatus", options =>
                {
                    options.HttpMessageHandlerFactory = _ => handler;
                })
                .WithAutomaticReconnect()
                .Build();

            // 绑定 SignalR 接收管道事件
            BindSignalREvents();
            
            // 异步启动被动接收连接
            _ = StartConnectionAsync();
        }

        private void BindSignalREvents()
        {
            _signalrConnection.On<string, double>("ReceiveTemperatureWarning", (deviceId, temp) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalRMessages.Add($"[警告] 设备 {deviceId} 温度异常：{temp}°C");
                });
            });

            _signalrConnection.On<int, string>("UpdateMachineState", (stateCode, description) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalRMessages.Add($"[状态更新] 代码:{stateCode} 描述:{description}");
                });
            });
            
            _signalrConnection.Closed += async (error) =>
            {
                StatusText = "Disconnected...";
                await Task.Delay(new Random().Next(0,5) * 1000);
                await StartConnectionAsync();
            };
        }

        public async Task StartConnectionAsync()
        {
            try
            {
                await _signalrConnection.StartAsync();
                StatusText = "Connected to SignalR Hub";
            }
            catch (Exception ex)
            {
                StatusText = $"Connection Error: {ex.Message}";
            }
        }

        public async Task StartStreamingDataAsync()
        {
            if (_streamCts != null)
            {
                // 如果已经在拉取，先取消
                _streamCts.Cancel();
                _streamCts.Dispose();
                _streamCts = null;
                GrpcMessages.Add("[gRPC] Stop streaming...");
                return;
            }

            _streamCts = new CancellationTokenSource();
            _vibrationSeries.Points.Clear();
            PlotModel.InvalidatePlot(true);
            GrpcMessages.Add("[gRPC] Start streaming historical high-frequency data...");

            try
            {
                var request = new DataRequest { DeviceId = 1, StartTime = DateTime.UtcNow.ToString("O") };
                using var call = _grpcClient.GetHistoricalData(request, cancellationToken: _streamCts.Token);
                
                int pointCount = 0;
                await foreach (var point in call.ResponseStream.ReadAllAsync(_streamCts.Token))
                {
                    pointCount++;
                    _vibrationSeries.Points.Add(new DataPoint(point.X, point.Y));

                    if (pointCount % 20 == 0)
                    {
                        PlotModel.InvalidatePlot(true);
                    }
                }
                
                GrpcMessages.Add("[gRPC] Data stream completed normally.");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                GrpcMessages.Add("[gRPC] Stream was manually cancelled.");
            }
            catch (Exception ex)
            {
                GrpcMessages.Add($"[gRPC Stream Error] {ex.Message}");
            }
            finally
            {
                _streamCts?.Dispose();
                _streamCts = null;
            }
        }

        public async Task SendStartCommandAsync()
        {
            try
            {
                var request = new CommandRequest { DeviceId = 1, Action = "START" };
                GrpcMessages.Add($"发送一元指令: {request.Action}...");
                var reply = await _grpcClient.SendCommandAsync(request);
                
                GrpcMessages.Add(reply.Success ? $"[指令成功] {reply.Message}" : $"[指令失败] {reply.Message}");
            }
            catch (Exception ex)
            {
                GrpcMessages.Add($"[指令异常] gRPC 调用失败: {ex.Message}");
            }
        }

        public async Task UploadBatchAsync()
        {
            GrpcMessages.Add("[gRPC] 开始上传轨迹点集 (Client Streaming) ...");
            
            // 确保每次切换回打点模式时，移除之前绘制的 SVG 线条，并将打点用的 Series 重新加回图表中
            TrajectoryPlotModel.Series.Clear();
            _trajectorySeries.Points.Clear();
            TrajectoryPlotModel.Series.Add(_trajectorySeries);
            TrajectoryPlotModel.InvalidatePlot(true);

            try
            {
                using var call = _grpcClient.UploadBatchTrajectory();

                double currentX = 0;
                double currentY = 0;

                // 模拟向服务端狂甩 100 个批次点
                for (int i = 0; i < 100; i++)
                {
                    currentX += Math.Cos(i * 0.1) * 2;
                    currentY += Math.Sin(i * 0.1) * 2 + 1;

                    _trajectorySeries.Points.Add(new ScatterPoint(currentX, currentY));
                    TrajectoryPlotModel.InvalidatePlot(true);

                    await call.RequestStream.WriteAsync(new PointData
                    {
                        X = currentX, Y = currentY, Z = 0.5, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    
                    // 模拟网络平滑上传
                    await Task.Delay(20); 
                }

                // 平滑结束发端流
                await call.RequestStream.CompleteAsync();
                
                // 拿回最终大报告
                UploadReply reply = await call.ResponseAsync;
                GrpcMessages.Add($"[gRPC] 上传完毕 -> {reply.Message} 总计收点: {reply.TotalPointsReceived}");
            }
            catch (Exception ex)
            {
                GrpcMessages.Add($"[gRPC UploadError] {ex.Message}");
            }
        }

        public async Task StartLiveTeleoperationAsync()
        {
            if (_isTeleopActive)
            {
                // 把开关关掉，等待正常结束
                _isTeleopActive = false;
                GrpcMessages.Add("[gRPC] 正在平滑断开遥操作双向信道...");
                return;
            }

            _teleopCts = new CancellationTokenSource();
            _isTeleopActive = true;
            GrpcMessages.Add("[gRPC] 开启硬核双向遥操作信道 (Bidirectional Streaming) ...");
            try
            {
                // 不使用 cancellation token 以避免抛出流异常导致的服务器错误输出。
                // 我们将通过 CompleteAsync() 处理优雅断开。
                using var call = _grpcClient.LiveTeleoperation();
                
                var readTask = Task.Run(async () => 
                {
                    try 
                    {
                        // 只需要等待到服务端也结束
                        await foreach(var feed in call.ResponseStream.ReadAllAsync())
                        {
                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                            {
                                ActualVelocity = feed.ActualVelocity;
                                ActualAngle = feed.ActualAngle;
                                StatusWarning = feed.StatusWarning;
                            });
                        }
                    }
                    catch (Exception) { /* 忽略各种读异常 */ }
                });

                // 主流程：模拟工人持续打摇杆，直到手动点击停止按钮
                try
                {
                    double currentAngle = 45.0;
                    while (_isTeleopActive)
                    {
                        await call.RequestStream.WriteAsync(new TeleoperationCmd 
                        {
                            TargetVelocity = 10.5, TargetAngle = currentAngle 
                        });
                        
                        // 稍微变换转角来模拟下发动作
                        currentAngle += 2.5;
                        if (currentAngle > 360) currentAngle = 0;

                        await Task.Delay(300, _teleopCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }

                // 优雅结束请求流
                await call.RequestStream.CompleteAsync();
                
                // 等待服务端流的关闭
                await readTask;
                GrpcMessages.Add("[gRPC] 遥操作会话已彻底结束。");
            }
            catch (Exception ex)
            {
                 GrpcMessages.Add($"[gRPC TeleopError] {ex.Message}");
            }
            finally
            {
                _isTeleopActive = false;
                _teleopCts?.Dispose();
                _teleopCts = null;
            }
        }

        public async Task TriggerRemoteAlarmAsync()
        {
            SignalRMessages.Add("[内部] 正在请求触发全厂警报...");
            try
            {
                var handler = new System.Net.Http.HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback = System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                using var http = new System.Net.Http.HttpClient(handler);
                
                var response = await http.PostAsync("https://localhost:7212/api/alarm/trigger", null);
                if (!response.IsSuccessStatusCode)
                {
                    SignalRMessages.Add("[WebAPI] 触发失败");
                }
            }
            catch (Exception ex)
            {
                 SignalRMessages.Add($"[WebAPI Error] {ex.Message}");
            }
        }

        public async Task SendLogToServerAsync()
        {
            try
            {
                if (_signalrConnection.State == HubConnectionState.Connected)
                {
                    string msg = $"Client Status Report at {DateTime.Now:HH:mm:ss}: Everything is running smooth.";
                    SignalRMessages.Add($"[SignalR] -> (发往服务端): {msg}");

                    // WPF主动调用后端的强类型Hub方法 "SendClientLogToServer"
                    await _signalrConnection.InvokeAsync("SendClientLogToServer", msg);
                }
                else
                {
                    SignalRMessages.Add("[SignalR] 当前未连接到后端，无法投递消息。");
                }
            }
            catch (Exception ex)
            {
                SignalRMessages.Add($"[SignalR Error] 推流失败: {ex.Message}");
            }
        }

        public async Task DownloadSvgAsync()
        {
            if (_svgCts != null)
            {
                _svgCts.Cancel();
                _svgCts.Dispose();
                _svgCts = null;
                GrpcMessages.Add("[gRPC] Stop SVG Streaming...");
                return;
            }

            _svgCts = new CancellationTokenSource();
            TrajectoryPlotModel.Series.Clear(); 
            var lineSeries = new LineSeries 
            { 
                Title = "SVG CNC Path", 
                Color = OxyColors.Yellow, 
                StrokeThickness = 3,
                LineJoin = LineJoin.Round
            };
            TrajectoryPlotModel.Series.Add(lineSeries);
            TrajectoryPlotModel.InvalidatePlot(true);
            
            GrpcMessages.Add("[gRPC] 请求服务端解析 SVG 滑稽并下发坐标流 (Server Streaming)...");

            try
            {
                var request = new SvgRequest { FileName = "doge3.svg" };
                using var call = _grpcClient.DownloadSvgTrajectory(request, cancellationToken: _svgCts.Token);
                
                await foreach (var point in call.ResponseStream.ReadAllAsync(_svgCts.Token))
                {
                    if (double.IsNaN(point.X) || double.IsNaN(point.Y))
                    {
                        // 服务器发出抬笔信号
                        lineSeries.Points.Add(DataPoint.Undefined);
                    }
                    else
                    {
                        // WPF的Y轴通常是向上的，或者OxyPlot的Y是向上的，而SVG的Y是向下的，所以加一个反转
                        lineSeries.Points.Add(new DataPoint(point.X, -point.Y));
                        TrajectoryPlotModel.InvalidatePlot(true);
                    }
                }
                
                GrpcMessages.Add("[gRPC] SVG 强类型流全面绘制完毕！");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                GrpcMessages.Add("[gRPC] SVG 绘制被手动中断。");
            }
            catch (Exception ex)
            {
                GrpcMessages.Add($"[gRPC SvgError] {ex.Message}");
            }
            finally
            {
                _svgCts?.Dispose();
                _svgCts = null;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object? parameter) => _execute(parameter);
    }
}

using Grpc.Net.Client;
using Industrial.Shared.Protos;
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
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Industrial.WpfClient.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private GrpcChannel? _grpcChannel;
        private DeviceControl.DeviceControlClient? _grpcClient;

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _statusText = "Disconnected";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> GrpcMessages { get; } = new ObservableCollection<string>();

        public ICommand StartCommand { get; }
        public ICommand StreamDataCommand { get; }
        public ICommand UploadBatchCommand { get; }
        public ICommand LiveTeleoperationCommand { get; }
        public ICommand DownloadSvgCommand { get; }
        
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }

        public PlotModel PlotModel { get; private set; }
        private LineSeries _vibrationSeries;
        
        public PlotModel TrajectoryPlotModel { get; private set; }
        private ScatterSeries _trajectorySeries;

        // --- 机器视觉相关属性 ---
        private ImageSource? _originalImageSource;
        public ImageSource? OriginalImageSource
        {
            get => _originalImageSource;
            set { _originalImageSource = value; OnPropertyChanged(); }
        }

        private ImageSource? _processedImageSource;
        public ImageSource? ProcessedImageSource
        {
            get => _processedImageSource;
            set { _processedImageSource = value; OnPropertyChanged(); }
        }

        public ICommand StartVisionCommand { get; }
        private bool _isVisionActive = false;
        private CancellationTokenSource? _visionCts;

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
            StartCommand = new RelayCommand(async _ => await SendStartCommandAsync(), _ => IsConnected);
            StreamDataCommand = new RelayCommand(async _ => await StartStreamingDataAsync(), _ => IsConnected);
            UploadBatchCommand = new RelayCommand(async _ => await UploadBatchAsync(), _ => IsConnected);
            LiveTeleoperationCommand = new RelayCommand(async _ => await StartLiveTeleoperationAsync(), _ => IsConnected);
            DownloadSvgCommand = new RelayCommand(async _ => await DownloadSvgAsync(), _ => IsConnected);
            StartVisionCommand = new RelayCommand(async _ => await StartVisionFeedAsync(), _ => IsConnected);

            ConnectCommand = new RelayCommand(async _ => await ConnectAsync(), _ => !IsConnected);
            DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected);

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

        }

        public async Task ConnectAsync()
        {
            try
            {
                StatusText = "Connecting...";
                
                var handler = new System.Net.Http.SocketsHttpHandler
                {
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = delegate { return true; }
                    }
                };
                var grpcOptions = new GrpcChannelOptions { HttpHandler = handler };
                _grpcChannel = GrpcChannel.ForAddress("https://localhost:7212", grpcOptions); 
                _grpcClient = new DeviceControl.DeviceControlClient(_grpcChannel);
                
                // 启动尝试连接
                await _grpcChannel.ConnectAsync();
                
                IsConnected = true;
                StatusText = "Connected";
                GrpcMessages.Add("[System] gRPC 连接建立成功。");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                StatusText = $"Connect Failed: {ex.Message}";
                GrpcMessages.Add($"[Error] 连接失败: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            if (_grpcChannel != null)
            {
                _grpcChannel.Dispose();
                _grpcChannel = null;
                _grpcClient = null;
            }
            IsConnected = false;
            StatusText = "Disconnected";
            GrpcMessages.Add("[System] 已断开独立连接。");
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
                using var call = _grpcClient!.GetHistoricalData(request, cancellationToken: _streamCts.Token);
                
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
                var reply = await _grpcClient!.SendCommandAsync(request);
                
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
                using var call = _grpcClient!.UploadBatchTrajectory();

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
                using var call = _grpcClient!.LiveTeleoperation();
                
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



        // --- 机器视觉 图像处理 (Image Generation & conversion) ---
        private RenderTargetBitmap CreateNoiseBitmap(int offset)
        {
            var rtb = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawRectangle(Brushes.DarkSlateBlue, null, new Rect(0, 0, 300, 300));
                ctx.DrawRectangle(Brushes.Crimson, null, new Rect(Math.Abs(Math.Sin(offset * 0.1)) * 200, Math.Abs(Math.Cos(offset * 0.1)) * 200, 100, 100));
                ctx.DrawLine(new Pen(Brushes.White, 3), new Point(0, offset % 300), new Point(300, offset % 300));
            }
            rtb.Render(visual);
            return rtb;
        }

        private byte[] ConvertBitmapToByteArray(BitmapSource bitmap)
        {
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        public async Task StartVisionFeedAsync()
        {
            if (_isVisionActive)
            {
                _isVisionActive = false;
                GrpcMessages.Add("[gRPC] Stop Vision Streaming...");
                return;
            }
            
            _isVisionActive = true;
            _visionCts = new CancellationTokenSource();
            GrpcMessages.Add("[gRPC] 开启双向视频流 Machine Vision 推流...");

            try
            {
                using var call = _grpcClient!.LiveMachineVision();

                // 接收并解包后台灰度图
                var readTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var feed in call.ResponseStream.ReadAllAsync())
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                using var ms = new MemoryStream(feed.ImageData.ToByteArray());
                                var image = new BitmapImage();
                                image.BeginInit();
                                image.CacheOption = BitmapCacheOption.OnLoad;
                                image.StreamSource = ms;
                                image.EndInit();
                                ProcessedImageSource = image;
                            });
                        }
                    }
                    catch (Exception) { }
                });

                // 本地不断生成彩色侦测帧
                int frameOffset = 0;
                while (_isVisionActive)
                {
                    RenderTargetBitmap? rtb = null;
                    byte[]? jpegBytes = null;
                    
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        rtb = CreateNoiseBitmap(frameOffset);
                        OriginalImageSource = rtb;
                        jpegBytes = ConvertBitmapToByteArray(rtb);
                    });

                    if (jpegBytes != null)
                    {
                        await call.RequestStream.WriteAsync(new VisionFrame
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            ImageData = Google.Protobuf.ByteString.CopyFrom(jpegBytes)
                        });
                    }

                    frameOffset += 10;
                    await Task.Delay(33, _visionCts.Token); // 约 30 FPS 上游推流
                }

                await call.RequestStream.CompleteAsync();
                await readTask;
                GrpcMessages.Add("[gRPC] Vision Streaming closed cleanly.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GrpcMessages.Add($"[gRPC VisionError] {ex.Message}");
            }
            finally
            {
                _isVisionActive = false;
                _visionCts?.Dispose();
                _visionCts = null;
            }
        }        public async Task DownloadSvgAsync()
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
                using var call = _grpcClient!.DownloadSvgTrajectory(request, cancellationToken: _svgCts.Token);
                
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

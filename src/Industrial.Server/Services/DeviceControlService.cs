using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Industrial.Shared.Protos;
using Microsoft.Extensions.Logging;
using Svg;
using Svg.Pathing;
using Svg.Transforms;
using System.Drawing;
using System.Drawing.Drawing2D;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using Google.Protobuf;

namespace Industrial.Server.Services;

/// <summary>
/// 实现了在 device.proto 中定义的 gRPC 服务接口。
/// 这里是后端处理核心工业控制指令和高频数据流的地方。
/// </summary>
public class DeviceControlService : DeviceControl.DeviceControlBase
{
    private readonly ILogger<DeviceControlService> _logger;

    public DeviceControlService(ILogger<DeviceControlService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 1. 简单 RPC (一元 RPC)
    /// 适用场景：下发明确的控制指令（如启动机床、急停等），并且需要立刻知道结果是否成功。
    /// 特点：类似于普通的 HTTP POST，但是底层序列化使用的是极快的 Protobuf 二进制格式。
    /// </summary>
    public override Task<CommandReply> SendCommand(CommandRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Received Command: {Action} for Device {DeviceId}", request.Action, request.DeviceId);
        
        // 模拟：执行具体的机器控制逻辑，例如通过 PLC 通讯协议去向真实下位机下发寄存器修改指令...
        
        // 模拟：构造并返回处理结果
        return Task.FromResult(new CommandReply
        {
            Success = true,
            Message = $"Command '{request.Action}' executed successfully."
        });
    }

    /// <summary>
    /// 2. 服务端流式 RPC (Server streaming RPC)
    /// 适用场景：WPF 客户端请求一次长达数分钟的连续波形数据，或者一次性获取巨大的历史记录表格。
    /// 特点：服务端可以将庞大的数据拆分成一个个小的 Message（如 PointData），像水流一样源源不断地推向客户端。
    ///       客户端收到一个就渲染一个。极大降低了服务端的内存压力（不需要在内存里拼接一个巨大的 List）。
    /// </summary>
    public override async Task GetHistoricalData(DataRequest request, IServerStreamWriter<PointData> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Streaming data for Device {DeviceId} starting from {StartTime}", request.DeviceId, request.StartTime);
        
        // 模拟生成轨迹数据以供流式返回（高频、大量数据）
        for (int i = 0; i < 5000; i++)
        {
            // 如果 WPF 客户端断开连接或者主动取消拉取数据，服务端这里能感知并立刻停止，省下了大量的算力
            if (context.CancellationToken.IsCancellationRequested)
                break;

            // 像写管道一样，立刻把生成的数据点推送过去。客户端那边就能实时收到了。
            await responseStream.WriteAsync(new PointData
            {
                X = i, // 横坐标：点序号或者时间
                Y = Math.Sin(i * 0.1) * 10 + (Random.Shared.NextDouble() * 2), // 模拟震动信号数据
                Z = Math.Cos(i * 0.1) * 10,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

        }
    }

    /// <summary>
    /// 3. 客户端流式 RPC (Client streaming RPC)
    /// 适用场景：客户端有庞大的数据需要上传（如一整套 CAD 解析出的十万个加工轨迹点），
    /// 一股脑塞进一个 HTTP 报文会导致超时或内存爆掉。
    /// 特点：客户端可以拆成很多个小包源源不断地发过来，服务端在接收时是一个异步迭代器。
    /// 当客户端发送完毕后，服务端返回一个最终的总结（UploadReply）。
    /// </summary>
    public override async Task<UploadReply> UploadBatchTrajectory(IAsyncStreamReader<PointData> requestStream, ServerCallContext context)
    {
        _logger.LogInformation("Receiving batch trajectory from client...");
        int pointsReceived = 0;

        // 异步等待客户端发送的源源不断的数据流
        await foreach (var point in requestStream.ReadAllAsync(context.CancellationToken))
        {
            pointsReceived++;
            // 模拟将该点落盘保存或写入下位机缓存
            // _logger.LogInformation("Received Point: X={x}, Y={y}", point.X, point.Y);
        }

        _logger.LogInformation("Batch upload completed. Total points: {Count}", pointsReceived);
        
        return new UploadReply
        {
            Success = true,
            TotalPointsReceived = pointsReceived,
            Message = "Trajectories cached successfully to CNC memory."
        };
    }

    /// <summary>
    /// 4. 双向流式 RPC (Bidirectional streaming RPC)
    /// 适用场景：最硬核的实时遥操作（如机械臂远程接管）。客户端需要高频下发打杆指令，
    /// 同时服务端也需要高频将机械臂当下的姿态、偏移量和告警传回来。两者完全解耦，两条管道独立运行。
    /// </summary>
    public override async Task LiveTeleoperation(IAsyncStreamReader<TeleoperationCmd> requestStream, IServerStreamWriter<TeleoperationFeed> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Bidirectional teleoperation session started.");
        
        // 绑定请求上下文的 CancellationToken，确保客户端异常断开时能被触发。
        // 由于我们需要在客户端 "完成上行请求" 时，主动停止 feedbackTask，因此创建一个 LinkedTokenSource
        using var feedbackCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        // 开辟一个后台任务：服务端主动不停地向客户端反馈下位机的当前状态
        var feedbackTask = Task.Run(async () =>
        {
            try 
            {
                while (!feedbackCts.IsCancellationRequested)
                {
                    await responseStream.WriteAsync(new TeleoperationFeed
                    {
                        ActualVelocity = Random.Shared.NextDouble() * 100, // 盲猜一个当前反馈速度
                        ActualAngle = Random.Shared.NextDouble() * 360,
                        StatusWarning = Random.Shared.Next(10) > 8 ? "Motor Heating!" : "Normal"
                    });
                    await Task.Delay(200, feedbackCts.Token); // 假设 5Hz 的机床状态回传刷新率
                }
            } 
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telemetry feedback task error.");
            }
        });

        // 在前台主流程：服务端不断地倾听客户端下发的控制摇杆指令
        try
        {
            await foreach (var cmd in requestStream.ReadAllAsync(context.CancellationToken))
            {
                _logger.LogInformation("TeleCmd Received -> Target Vel: {Vel}, Target Ang: {Ang}", cmd.TargetVelocity, cmd.TargetAngle);
                // 这里调用底层驱动代码，强行干预机床转角和速度...
            }
            // 如果执行到这里，说明客户端调用了 requestStream.CompleteAsync() 平滑结束了上行通道。
            _logger.LogInformation("Client finished sending teleoperation commands.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Teleoperation was aborted by the client.");
        }
        catch (System.IO.IOException)
        {
            _logger.LogWarning("Teleoperation stream reset by the client.");
        }
        finally
        {
            // 当上行通道结束（不管是正常结束还是异常断开），主动通知反馈任务结束下行推流。
            feedbackCts.Cancel();
        }

        // 等待反馈任务平稳收尾
        await feedbackTask;
        _logger.LogInformation("Bidirectional teleoperation session cleanly ended.");
    }

    public override async Task DownloadSvgTrajectory(SvgRequest request, IServerStreamWriter<PointData> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Client requested SVG parse: {FileName}", request.FileName);
        
        try
        {
            // 通过 AppContext 的目录找到 Assets 文件夹
            var safePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", request.FileName));
            if (!File.Exists(safePath)) 
            {
                _logger.LogWarning("File not found: {Path}", safePath);
                throw new RpcException(new Status(StatusCode.NotFound, "SVG file not found on server."));
            }

            // 使用 Svg 包解析复杂的包含 <path d="..."> 的 SVG 文件
            var svgDoc = SvgDocument.Open(safePath);
            var paths = svgDoc.Descendants().OfType<SvgPath>();
            
            foreach (var path in paths)
            {
                if (path.PathData == null) continue;

                float currentX = 0;
                float currentY = 0;
                float startX = 0;
                float startY = 0;

                foreach (var segment in path.PathData)
                {
                    if (context.CancellationToken.IsCancellationRequested) break;
                    
                    float offsetX = segment.IsRelative ? currentX : 0;
                    float offsetY = segment.IsRelative ? currentY : 0;

                    if (segment is SvgMoveToSegment moveSegment)
                    {
                        // MoveTo (M 命令) 表示抬笔到了新位置
                        await responseStream.WriteAsync(new PointData { X = double.NaN, Y = double.NaN });
                        
                        currentX = moveSegment.End.X + offsetX;
                        currentY = moveSegment.End.Y + offsetY;
                        startX = currentX;
                        startY = currentY;
                        await SendPointAsync(responseStream, currentX, currentY, path.Transforms);
                    }
                    else if (segment is SvgLineSegment lineSegment)
                    {
                        currentX = lineSegment.End.X + offsetX;
                        currentY = lineSegment.End.Y + offsetY;
                        await SendPointAsync(responseStream, currentX, currentY, path.Transforms);
                    }
                    else if (segment is SvgCubicCurveSegment cubicSegment)
                    {
                        float cp1X = cubicSegment.FirstControlPoint.X + offsetX;
                        float cp1Y = cubicSegment.FirstControlPoint.Y + offsetY;
                        float cp2X = cubicSegment.SecondControlPoint.X + offsetX;
                        float cp2Y = cubicSegment.SecondControlPoint.Y + offsetY;
                        float endX = cubicSegment.End.X + offsetX;
                        float endY = cubicSegment.End.Y + offsetY;
                        
                        float startXCurve = currentX;
                        float startYCurve = currentY;

                        // 高精度解析：这是一条三次贝塞尔曲线段，我们在后台插值运算生成 20 个过渡点
                        int steps = 20;
                        for (int i = 1; i <= steps; i++)
                        {
                            float t = i / (float)steps;
                            float ptX = (float)(Math.Pow(1 - t, 3) * startXCurve + 3 * Math.Pow(1 - t, 2) * t * cp1X + 3 * (1 - t) * Math.Pow(t, 2) * cp2X + Math.Pow(t, 3) * endX);
                            float ptY = (float)(Math.Pow(1 - t, 3) * startYCurve + 3 * Math.Pow(1 - t, 2) * t * cp1Y + 3 * (1 - t) * Math.Pow(t, 2) * cp2Y + Math.Pow(t, 3) * endY);
                            await SendPointAsync(responseStream, ptX, ptY, path.Transforms);
                        }
                        currentX = endX;
                        currentY = endY;
                    }
                    else if (segment is SvgQuadraticCurveSegment quadSegment)
                    {
                        float cpX = quadSegment.ControlPoint.X + offsetX;
                        float cpY = quadSegment.ControlPoint.Y + offsetY;
                        float endX = quadSegment.End.X + offsetX;
                        float endY = quadSegment.End.Y + offsetY;
                        
                        float startXCurve = currentX;
                        float startYCurve = currentY;

                        // 二次贝塞尔插值
                        int steps = 20;
                        for (int i = 1; i <= steps; i++)
                        {
                            float t = i / (float)steps;
                            float ptX = (float)(Math.Pow(1 - t, 2) * startXCurve + 2 * (1 - t) * t * cpX + Math.Pow(t, 2) * endX);
                            float ptY = (float)(Math.Pow(1 - t, 2) * startYCurve + 2 * (1 - t) * t * cpY + Math.Pow(t, 2) * endY);
                            await SendPointAsync(responseStream, ptX, ptY, path.Transforms);
                        }
                        currentX = endX;
                        currentY = endY;
                    }
                    else if (segment is SvgClosePathSegment closeSegment)
                    {
                        // 闭合路径，强制连线回到起点
                        currentX = startX;
                        currentY = startY;
                        await SendPointAsync(responseStream, currentX, currentY, path.Transforms);
                    }
                }
                
                // 每一段 path 结束，抬笔
                await responseStream.WriteAsync(new PointData { X = double.NaN, Y = double.NaN });
            }
            
            _logger.LogInformation("SVG transfer completed successfully.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("SVG Stream was cancelled by client.");
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            _logger.LogError(ex, "Error parsing SVG.");
            throw new RpcException(new Status(StatusCode.Internal, "Server failed to process SVG"));
        }
    }

    private async Task SendPointAsync(IServerStreamWriter<PointData> responseStream, float x, float y, SvgTransformCollection? transforms)
    {
        var pts = new System.Drawing.PointF[] { new System.Drawing.PointF(x, y) };

        // 应用形变 (包括用户图片中导出的旋转、平移等属性)
        if (transforms != null && transforms.Count > 0)
        {
            using var matrix = transforms.GetMatrix();
            matrix.TransformPoints(pts);
        }

        await responseStream.WriteAsync(new PointData
        {
            X = pts[0].X,
            Y = pts[0].Y,
            Timestamp = DateTime.UtcNow.Ticks
        });
        await Task.Delay(5); // 控制下发流速
    }

    /// <summary>
    /// 6. 双向流式 RPC (Bidirectional streaming RPC)
    /// 相机图像帧处理。一边接收客户端上传的画面帧，一边服务端调用ImageSharp做实时灰度转换处理再推回
    /// </summary>
    public override async Task LiveMachineVision(IAsyncStreamReader<VisionFrame> requestStream, IServerStreamWriter<VisionFrame> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Live Machine Vision processing stream started.");
        try
        {
            await foreach (var frame in requestStream.ReadAllAsync(context.CancellationToken))
            {
                if (frame.ImageData == null || frame.ImageData.Length == 0) continue;

                // 在服务端极速处理图像
                using var image = SixLabors.ImageSharp.Image.Load(frame.ImageData.ToByteArray());
                image.Mutate(x => x.Grayscale()); // 转换为灰度图

                using var ms = new MemoryStream();
                image.Save(ms, new JpegEncoder()); // 重新保存为 Jpeg
                
                // 将灰度图流推回给客户端
                await responseStream.WriteAsync(new VisionFrame
                {
                    Timestamp = frame.Timestamp,
                    ImageData = ByteString.CopyFrom(ms.ToArray())
                });
            }
            _logger.LogInformation("Machine Vision Stream Ended.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Machine Vision Stream Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Machine Vision Stream Error.");
        }
    }
}

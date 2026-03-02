# 工业级前后端彻底分离架构演示 (gRPC + SignalR + Vue3 + WPF)

本系统旨在展示一种**高性能、跨平台、前后端彻底解耦**的工业级上位机（或工控看板）通信架构参考模型。包含 .NET WPF 本地重型客户端与 Vue 3 Web 轻数据大屏。

---

## 📁 一、 项目代码结构

本项目由四个核心工程组成，构成完整的微服务与强类型契约体系：

- **`Industrial.Shared` (共享类库)**: 存放核心的 `.proto` (Protobuf) 契约文件。负责定义 gRPC 的请求/响应数据结构，是跨语言和跨工程双向沟通的唯一权威桥梁。
- **`Industrial.Server` (ASP.NET Core Server)**: 核心主控服务端。承载了所有的 gRPC 服务 (`DeviceControlService`)、SignalR 集线器 (`DeviceStatusHub`)、后台长连接仿真数据服务 (`DataSimulationWorker`) 以及提供外部拉取的 HTTP API。
- **`Industrial.WpfClient` (.NET 8 WPF)**: 本地重型客户端。使用 OxyPlot 绘制高频图表，通过 gRPC 的四种核心模式（一元调用、客户端流、服务端流、双向流）直接操控底层的硬件级通讯。
- **`Industrial.Vue` (Vue 3 + Vite)**: 远程 Web 大数据看板。通过 SignalR WebSocket 接收服务端下发的海量毫秒级模拟机床数据流并利用 ECharts 生成实时动画图表。

---

## 🚀 二、 如何使用命令行启动每个项目以及启动后操作

推荐使用终端命令行 (CLI) 启动每一个独立微服务。请打开三个单独的 Terminal 工具。

### 1. 启动主干服务端 (Server)
进入项目根目录的终端，执行：
```bash
cd src/Industrial.Server
dotnet run
```
*启动成功后监听在 `https://localhost:7212`。此常驻进程必须保持运行，它是整个系统的数据心脏。*

### 2. 启动 WPF 工业操作台 (WpfClient)
在第二个终端中，执行：
```bash
cd src/Industrial.WpfClient
dotnet run
```
**操作指南**：
1. 界面开启后，优先点击右上角的 **`Connect`** 以接通全局 gRPC 底层通道。
2. 在 `Vibration Monitoring` 页签点击 **`Server Stream (Data)`**，欣赏源源不断的工业级毫秒震动曲线推送渲染。
3. 在 `Trajectory & SVG Mapping` 页签点击 **`Download SVG Path`**，由后端通过矩阵变换剥离打碎 Svg 为极密坐标流向客户端传输并引发全屏绘制。
4. 在 `Machine Vision & AI` 页签点击 **`Start Real-time Vision (Bidi Stream)`**，你将看到一个由 WPF 本地生成的假想“机械臂视觉侦测框”连续画面。以高达 30 帧的速率不断通过 gRPC 上传至服务端；服务端利用 `SixLabors.ImageSharp` 引擎进行工业级灰度转化和噪点过滤后，即时双向流式回传给 WPF 并在右侧屏幕渲染！
5. 在最右侧面板的底部，点击 **`Bidi Stream`** 体验全双工遥控操作同步：左发请求、右接状态。

### 3. 启动 Vue Web 可视化大屏 (Vue UI)
在第三个包含 Node.js 环境的终端中，执行：
```bash
cd src/Industrial.Vue
npm install
npm run dev
```
**操作指南**：
1. 浏览器打开界面 `http://localhost:5173`。
2. 点击左上角的 **`[打开链接]`** 发起对后端 SignalR 长连接的握手请求。
3. 连接成功后，右侧面板将开始涌入系统日志追踪事件。左侧每 3s/4s 极速刷入机床状态参数、温度预警大屏曲线图表开始绘制。
4. 通过右侧操作面板可以向下位机的服务端发送“下发指令”。点击 **`⟳ 手动拉取实时机床温度`** 按钮来插队发起一次独立的 WebAPI HTTP 拉取指令。

---

## ⚙️ 三、 代码原理和项目架构原理解析

本架构彻底抛弃了单纯的传统 REST 轮询方案，采用**高低频分离的双信道传输**。

### 1. 宏观项目混合架构模式

```mermaid
graph TD
    subgraph ClientWPF ["本地重型端 (WPF / .NET 8)"]
        UI_WPF["WPF UI (OxyPlot实时渲染)"]
        VM["MainViewModel (GrpcClientHandler)"]
        UI_WPF -- "DataBinding" --- VM
    end

    subgraph ClientVue ["Web 轻量看板 (Vue 3 + ECharts)"]
        UI_Vue["科幻数据大屏"]
        SignalR_JS["@microsoft/signalr.js"]
        UI_Vue -- "Reactive Ref" --- SignalR_JS
    end

    subgraph Contract ["跨语言契约池"]
        Proto["device.proto (RPC 协议基石)"]
    end

    subgraph Server ["核心主控服务端 (ASP.NET Core)"]
        GrpcService["DeviceControlService (重度低延迟指令)"]
        SignalRHub["DeviceStatusHub (高频被动广播)"]
        Worker["DataSimulationWorker (后台时钟仿真发动机)"]
        Worker -->|"同服内部事件派发"| SignalRHub
    end

    VM -.->|"编译时拦截生成"| Proto
    GrpcService -.->|"编译时自动生成"| Proto

    VM <======>|"1. gRPC 通道 (HTTP/2 二进制强连接)"| GrpcService
    SignalR_JS <====>|"2. SignalR 通道 (WebSocket 持久化长链接)"| SignalRHub
    
    %% 连接说明样式
    classDef grpc fill:#e1f5fe,stroke:#03a9f4,stroke-width:2px;
    classDef signalr fill:#e8f5e9,stroke:#4caf50,stroke-width:2px;
    classDef vue fill:#fce4ec,stroke:#e91e63,stroke-width:2px;
    
    class GrpcService,VM grpc;
    class SignalRHub,SignalR_JS signalr;
    class ClientVue,UI_Vue vue;
```

---

### 2. 操作对应服务的原理详解

底层核心依赖由于采用了 `.proto` 强规范与多通道设计，使得上层能衍生出下面五大典型工业控制绝招：

#### 🔘 一元调用 (Unary RPC) —— 对应: `Send START Command` 按钮
发送明确控制指令。头部压缩和去泛型化解析速度远超普通 JSON HTTP。
```mermaid
sequenceDiagram
    participant WPF as WpfClient
    participant Server as gRPC Server
    WPF->>Server: 1. SendCommandAsync (Start指令)
    note over Server: 毫秒级触发微控指令
    Server-->>WPF: 2. Return Response (Success/Fail 枚举)
```

#### 🔘 服务端流式推送 (Server Streaming) —— 对应: `Server Stream` / `Download SVG` 按钮
应对上万级别的历史数组拉取、曲线下行方案，避免了巨大的超时压力。流式读取可以在完整内容全送达前就一边接收一边通知 UI 动画渲染。如果客户端掉线，服务器流立即感应熔断终止浪费。

```mermaid
sequenceDiagram
    participant WPF as WpfClient (OxyPlot)
    participant Server as ASP.NET Core 服务端
    participant Hardware as 模拟传感器/运算单元

    WPF->>Server: 1. GetHistoricalData (参数 Device=1)
    note right of Server: 2. Server 开辟响应流水线 (IAsyncStreamReader)
    
    loop 源源不断的微元推流
        Hardware-->>Server: 获取到底层震点 / Svg微积分切片坐标
        Server-->>WPF: 3. await responseStream.WriteAsync(片段)
        WPF->>WPF: 4. client.ResponseStream.ReadAllAsync() 累加
        WPF->>WPF: 每集齐 N 个点即异步通知 UI 绘制点位
    end
    note over WPF,Server: 展现水滴石穿的全过程可视化
```

#### 🔘 客户端流式推送 (Client Streaming) —— 对应: `Client Stream` 按钮
主要用于大文件、庞大 CAD 轨迹组传输给下位机，避免把系统内存撑爆。
```mermaid
sequenceDiagram
    participant WPF as WpfClient
    participant Server as ASP.NET Core 服务端

    WPF->>Server: 1. 发起 UploadTrajectoryAsync 建立持久上行
    note over WPF,Server: 客户端抢占并控制发送主脉络
    loop 分批次传输重型包裹
        WPF-->>Server: 2. WriteAsync(PointData 1)
        WPF-->>Server: WriteAsync(PointData 2)... 
    end
    WPF->>Server: 3. 释放 CompleteAsync() (完毕封口)
    Server-->>WPF: 4. Final: 归纳整理返回“已接收包总数验证”
```

#### 🔘 双向独立流 (Bidi Streaming) —— 对应: `Bidi Stream` 按钮
在同一个物理连接信道内，读写彻底解耦实现异步“对冲通讯”。一方模拟人类操纵摇杆，一方反馈下发机器真实现状转速，用于低延迟遥操作。
```mermaid
sequenceDiagram
    participant WPF as WpfClient (发信Task)
    participant Server as Server (闭环驱动Task)
    participant WPF_UI as WpfClient (界层UI接收Task)

    note over WPF, Server: 发送与接收并行不干涉的双向网桥隧道
    
    par [Client 单侧全速打摇杆]
        loop 直至人类结束
            WPF-->>Server: 1. 发射 TeleoperationCmd 强干扰量
        end
    and [Server 单侧全速传状态]
        loop 服务未终结
            Server-->>WPF_UI: 2. 回传 TeleoperationFeed 实况传感器数据
            WPF_UI->>WPF_UI: 异步抽离更新至画面帧
        end
    end
```

#### 🔘 WebSockets 全域双工广播 (SignalR + Vue) —— 对应: `打开链接` / `手动拉取温度` 按钮
由 Vue 渲染器无缝衔接 C# 的守护工作线程，当发生任何改变，远端直接操纵网页内 DOM，而非被动等待刷新导致真空期。

```mermaid
sequenceDiagram
    participant Worker as DataSimulationWorker (守护任务)
    participant Hub as DeviceStatusHub (SignalR 集线器)
    participant Vue as Vue 3 浏览器中继
    participant API as WebAPI 控制器接口

    Vue->>Hub: 1. [握手] .build().start() 建立 WebSocket 通道
    
    par 后端底层 3,4,5秒级并轨循环派发
        loop 三个隔离后台死循环时钟
            Worker-->>Hub: 2. Clients.All.SendAsync
            Hub-->>Vue: 3. [Push] Vue connection.on 直接覆盖渲染 ref 数据
        end
    and HTTP API 降级兼容并强塞推流
        Vue->>API: 4. [HTTP POST] /api/temperature/push_current (人类强击事件)
        API-->>Hub: 5. 获取 IHubContext<T> 集成
        Hub-->>Vue: 6. [Push 即时推流] 指定强发一条温度流给浏览器
        API-->>Vue: 7. 返回 200 HTTP Result OK
    end
```

#### 🔘 双向流机器视觉与AI极速处理 (Live Machine Vision) —— 对应 `Start Real-time Vision` 按钮
这个特性展示了针对真正的“工业相机”所带来的吞吐量压力。WPF 每秒生成 30 张高分辨率静态帧，通过 `ByteString` 直接拍进双向流。远端 ASP.NET 承接后不落地，使用 ImageSharp 在内存中完成工业级图像灰度萃取并立刻压回 Jpeg 返回。
```mermaid
sequenceDiagram
    participant WPF as WpfClient (相机视频流模拟)
    participant Server as ASP.NET Core (ImageSharp图像引擎)

    note over WPF, Server: 1. 打开极其硬核的高清无缝双向管道

    par 视频上行管线
        loop 30帧/秒
            WPF->>WPF: 捕获或伪造摄像头彩色图像并转码Jpeg
            WPF-->>Server: 2. WriteAsync(VisionFrame Bytes)
        end
    and 处理与渲染截回管线
        loop
            Server->>Server: 3. 拦截 Byte[] 实施极速灰度 Mutate 处理
            Server-->>WPF: 4. 回写灰度图像流
            WPF->>WPF: 5. Decode 并同步渲染于 UI 的 Right Box
        end
    end
```

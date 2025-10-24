# BassPlayerSharp - 基于C#的音频播放程序
BassPlayerSharp 是一款采用 **C# 开发**的开源音频播放程序，基于 `un4seen bass` 音频处理库构建，主打高性能播放控制、多输出模式支持与进程间通信能力，为用户提供稳定、低延迟的音频播放体验。


## 项目基本信息
| 项目维度 | 详情 |
|----------|------|
| 项目名称 | BassPlayerSharp |
| 托管平台 | Gitee（仓库地址：[https://gitee.com/people_1/bass-player-sharp](https://gitee.com/people_1/bass-player-sharp)）<br />Github （仓库地址：[https://github.com/Johnwikix/BassPlayerSharp](） |
| 开发语言 | C# |
| 核心依赖 | `un4seen bass` |


## 核心功能与特性
BassPlayerSharp 围绕“高性能播放”与“灵活扩展”两大核心设计，关键特性如下：

### 1. 全场景音频播放能力
- **多格式支持**：原生支持普通音频文件（如 MP3、WAV等）及高解析度 DSD 格式（.dsf、.dff），通过 `ManagedBass.Dsd` 模块实现 DSD 原生解码与 DoP（DSD over PCM）输出。
- **多输出模式**：适配不同播放场景，支持 5 种输出模式：
  - DirectSound（默认，兼容主流设备）
  - WASAPI Shared（系统共享模式，低资源占用）
  - WASAPI Exclusive Push/Event（独占模式，低延迟高保真）
  - ASIO（专业音频设备模式，极低延迟，支持 DSD 原生输出）
- **播放控制**：支持播放/暂停、进度跳转、音量调节、播放模式切换（列表循环等），并提供播放状态实时反馈。

### 2. 专业音频增强
- **10 段均衡器**：内置 32Hz~16kHz 全频段均衡器，支持增益调节（-60dB~0dB）、开启/关闭/重置操作，可通过 JSON 配置批量更新均衡器参数。
- **音量安全保护**：在 WASAPI 独占模式下自动启用音量安全机制，避免音量骤升导致设备损坏或听觉不适，同时支持音量线性/分贝（dB）值转换。

### 3. 高性能进程间通信（IPC）
基于 **共享内存（Memory-Mapped File）** 实现进程间通信，配合信号量同步机制，支持外部程序（如 UI 客户端）与播放核心的低延迟交互：
- **请求-响应机制**：外部客户端可发送播放、音量调节、进度查询等指令，核心服务返回执行结果。
- **实时通知**：播放状态变化（如播放/暂停切换、播放结束）、音量更新等事件通过通知机制主动推送给客户端。
- **性能优化**：采用预分配缓冲区、`Span<T>` 零分配序列化、`ArrayPool` 内存复用，减少 GC 压力，提升通信效率。


## 项目目录结构说明
```
BassPlayerSharp/
├─ Libraries/          # 外部依赖库、工具类（含计时器逻辑迁移后的代码）
├─ Manager/            # 资源管理模块（如 Bass 库初始化与释放，已优化魔法值）
├─ Model/              # 数据模型定义（如 IpcSetting、RequestMessage、ResponseMessage 等）
├─ Properties/         # 项目属性配置
│  └─ PublishProfiles/ # 发布配置文件（近期更新）
├─ Service/            # 核心服务层（音频播放与 IPC 通信的实现核心）
│  ├─ MmpIpcService.cs # 共享内存 IPC 服务：处理客户端指令、发送实时通知
│  └─ PlayBackService.cs # 播放控制服务：音频解码、输出模式切换、均衡器管理
├─ .gitignore          # Git 忽略文件（近期更新）
├─ BassPlayerSharp.csproj # 项目主配置文件（含 GC 优化配置）
├─ BassPlayerSharp.csproj.user # 用户个性化配置
├─ Program.cs          # 程序入口（初始化服务、启动 IPC 与播放核心，含音量保护）
└─ icon.ico            # 程序图标（关联 GC 优化相关资源）
```


## 核心 Service 层解析
`Service` 目录是项目的功能核心，包含两大关键文件，负责音频播放控制与进程间通信，以下为详细解析：

### 1. MmpIpcService.cs - 共享内存 IPC 服务
#### 功能定位
作为外部客户端（如 UI 界面）与播放核心（`PlayBackService`）的“通信桥梁”，基于共享内存实现低延迟指令交互与事件通知，支持多进程协作。

#### 核心结构与逻辑
| 核心组件 | 作用 |
|----------|------|
| `SharedMemoryData` 结构体 | 定义共享内存缓冲区：请求缓冲区（4096B）、响应缓冲区（1024B）、通知缓冲区（1024B），固定大小避免内存溢出。 |
| 信号量（Semaphore） | 3 个信号量分别控制“请求就绪”“响应就绪”“通知就绪”，确保进程间同步，避免数据竞争。 |
| 客户端存活监控 | 通过 `ClientAliveMutexName` 互斥量监控客户端存活状态，客户端退出时自动停止服务，释放资源。 |
| 指令处理流程 | 1. 监听“请求就绪”信号量；<br>2. 从共享内存读取 JSON 格式指令（如 `Play`、`Volume`）；<br>3. 调用 `PlayBackService` 执行指令；<br>4. 将结果写入共享内存，释放“响应就绪”信号量。 |
| 实时通知 | 提供 `SendNotification()` 方法，由 `PlayBackService` 触发（如播放状态变化、音量更新），主动向客户端推送事件。 |

#### 性能优化点
- **零分配序列化**：通过 `Span<T>` 解析指令，减少内存分配。
- **内存复用**：通过 `ArrayPool<byte>.Shared` 复用字节数组，降低 GC 压力；预分配 `_readBuffer`、`_jsonBufferWriter` 等缓冲区，避免频繁创建销毁。


### 2. PlayBackService.cs - 播放服务
#### 功能定位
音频播放的“核心引擎”，封装 `un4seen bass` 库的底层能力，实现音频解码、输出模式切换、均衡器控制、播放状态管理等核心逻辑。

#### 核心功能模块
| 模块 | 关键能力 | 实现细节 |
|------|----------|----------|
| 音频解码与流管理 | 支持普通音频与 DSD 格式解码，根据输出模式创建对应音频流（如 WASAPI 独占模式创建解码流，DirectSound 创建普通播放流）。 | 通过 `Bass.CreateStream()`/`BassDsd.CreateStream()` 创建流，结合 `SyncFlags.End` 监听播放结束事件。 |
| 多输出模式切换 | 适配 5 种输出模式，自动初始化对应音频设备（如 WASAPI 初始化、ASIO 设备配置）。 | 由 `SwitchDevice()` 统一处理设备切换，`InitializePlayback()` 初始化播放参数（如采样率、声道数）。 |
| 10 段均衡器 | 支持均衡器开启/关闭、增益调节、参数重置，覆盖 32Hz~16kHz 全频段。 | 基于 `ManagedBass.Fx` 的 `PeakEQ` 类实现，`_bandIndices` 存储各频段索引，`SetEqualizerGain()` 实时更新增益。 |
| 音量与进度控制 | 支持线性音量调节、dB 值转换，进度跳转与微调（精确到秒）。 | 音量调节：根据输出模式调用 `BassWasapi.SetVolume()`/`Bass.ChannelSetAttribute()`；进度控制：通过 `ChannelSeconds2Bytes()` 转换时间与字节位置。 |
| 资源释放 | 实现 `IDisposable`，释放音频流、均衡器、Bass 库资源，避免内存泄露。 | `DisposeStream()` 释放当前音频流，`DisposeEq()` 销毁均衡器，`BassManager.Free()` 释放 Bass 库资源。 |

#### 格式与设备支持
- **支持格式**：mp3、wav、flac、wma、aac、ogg、oga、aiff、aif、m4a、ape、opus、wv等普通格式；DSD 格式（dsf、.dff，支持 DoP 输出与原生 DSD 输出）。
- **设备适配**：自动识别系统音频设备，支持指定输出设备 ID（`BassOutputDeviceId`/`BassASIODeviceId`），低延迟模式（ASIO、WASAPI Exclusive）适配专业音频设备。


## 快速开始
### 1. 环境准备
- **开发工具**：推荐 Visual Studio 2026（支持 C# 最新语法与 .NET 10.0）。
- **框架依赖**：.NET 10.0 及以上（参考 `BassPlayerSharp.csproj` 中的 `TargetFramework` 配置）。

### 2. 代码拉取
通过 Git 克隆项目到本地：
```bash
git clone https://github.com/Johnwikix/BassPlayerSharp.git
cd bass-player-sharp
```

### 3. 编译与运行
1. 用开发工具打开 `BassPlayerSharp.csproj` 项目文件。
2. 还原 NuGet 依赖。
3. 直接编译并运行：
   - 程序启动后，`MmpIpcService` 会初始化共享内存与信号量，等待客户端指令。
   - 可通过自定义客户端（或调试代码）发送 `Play` 指令（指定音频文件路径），触发播放逻辑。

### 4. 测试 IPC 通信
外部客户端可通过以下步骤与核心服务通信：
1. 创建与 `MmpIpcService` 同名的共享内存（`BassPlayerSharp_SharedMemory`）与信号量。
2. 向共享内存写入 JSON 格式的请求指令
3. 释放“请求就绪”信号量（`BassPlayerSharp_RequestReady`），等待核心服务响应。
4. 监听“响应就绪”信号量，读取共享内存中的执行结果。


## 问题反馈与支持
若在使用或开发中遇到问题，可通过以下方式反馈：
1. **Gitee Issues**：在项目仓库的 [Issues](https://gitee.com/people_1/bass-player-sharp/issues) 页面提交问题，需包含：
   - 环境信息（操作系统版本、.NET 版本、音频设备型号）。
   - 复现步骤（如触发问题的操作、输入指令）。
   - 错误日志（如控制台输出、异常堆栈信息）。
2. **代码讨论**：在项目“代码”页面针对具体文件（如 `Service/PlayBackService.cs`）发起讨论，与维护者交流技术细节。
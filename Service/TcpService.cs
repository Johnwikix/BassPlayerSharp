using BassPlayerSharp.Model;
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BassPlayerSharp.Service
{
    // 定义内存中数据结构（Server/Client 共享）
    // 为了简化和保持与原方法中的JSON通信一致性，我们定义一个固定大小的缓冲区。
    // 在实际应用中，你需要处理变长消息和序列化/反序列化的开销。
    [StructLayout(LayoutKind.Sequential)]
    public struct SharedMemoryData
    {
        // 预留给 JSON 消息的最大字节数
        public const int MaxMessageSize = 4096;
        public const int MaxResponseSize = 1024;

        // 用于请求消息的缓冲区
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxMessageSize)]
        public byte[] RequestBuffer;

        // 用于响应消息的缓冲区
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxResponseSize)]
        public byte[] ResponseBuffer;

        // 新增：通知缓冲区
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxResponseSize)]
        public byte[] NotificationBuffer;

        public SharedMemoryData()
        {
            RequestBuffer = new byte[MaxMessageSize];
            ResponseBuffer = new byte[MaxResponseSize];
            NotificationBuffer = new byte[MaxResponseSize];
        }
    }

    public class TcpService : IDisposable
    {
        private readonly PlayBackService playBackService;

        // 共享内存名称
        private const string MmfName = "BassPlayerSharp_SharedMemory";
        // 信号量名称
        private const string RequestSemaphoreName = "BassPlayerSharp_RequestReady";
        private const string ResponseSemaphoreName = "BassPlayerSharp_ResponseReady";
        private const string NotificationSemaphoreName = "BassPlayerSharp_NotificationReady";
        private const string ClientAliveMutexName = "WinUIMusicPlayer_SingleInstanceMutex";
        // 共享内存和访问器
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;

        // 同步对象：信号量
        // RequestReadySemaphore: 由客户端释放，Server等待，表示有新请求
        private Semaphore _requestReadySemaphore;
        // ResponseReadySemaphore: 由Server释放，Client等待，表示有新响应
        private Semaphore _responseReadySemaphore;
        private Semaphore _notificationReadySemaphore;
        // 共享内存总大小 (包含请求和响应缓冲区)
        private static readonly long MmfSize = SharedMemoryData.MaxMessageSize  + SharedMemoryData.MaxResponseSize * 2;

        // 请求缓冲区起始偏移量
        private const long RequestBufferOffset = 0;
        // 响应缓冲区起始偏移量
        private static readonly long ResponseBufferOffset = SharedMemoryData.MaxMessageSize;
        private static readonly long NotificationBufferOffset = SharedMemoryData.MaxMessageSize + SharedMemoryData.MaxResponseSize;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _listenerTask;
        private Task _clientMonitorTask;

        public TcpService()
        {
            this.playBackService = new PlayBackService(this);
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task StartAsync()
        {
            Console.WriteLine("SharedMemoryService starting...");
            try
            {
                // 1. 创建/打开 MMF 和信号量
                // 服务器端创建 MMF 和信号量。
                // MMF：创建或打开，大小为 MmfSize
                _mmf = MemoryMappedFile.CreateOrOpen(MmfName, MmfSize);
                _accessor = _mmf.CreateViewAccessor(0, MmfSize);

                // 信号量：初始计数都为 0 (无请求/响应)，最大计数为 1 (只允许一个通知)
                // true 表示创建新的信号量；如果已存在则打开
                _requestReadySemaphore = new Semaphore(0, 1, RequestSemaphoreName, out bool requestCreatedNew);
                _responseReadySemaphore = new Semaphore(0, 1, ResponseSemaphoreName, out bool responseCreatedNew);
                _notificationReadySemaphore = new Semaphore(0, 1, NotificationSemaphoreName, out bool notificationCreatedNew);
                if (!requestCreatedNew || !responseCreatedNew)
                {
                    // 如果信号量不是新创建的，则说明已有其他进程在运行（Client）
                    Console.WriteLine("Warning: Semaphores already exist. Ensure no other server is running.");
                }

                Console.WriteLine($"Server is ready for shared memory communication. MMF: {MmfName}");

                // 2. 启动监听任务
                _listenerTask = Task.Run(() => ListenForRequestsAsync(_cancellationTokenSource.Token));
                _clientMonitorTask = Task.Run(() => MonitorClientAliveAsync(_cancellationTokenSource.Token));
                await Task.WhenAny(_listenerTask, _clientMonitorTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                Dispose();
                Console.WriteLine("SharedMemoryService stopped.");
            }
        }

        private async Task MonitorClientAliveAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Client alive monitor started...");

            // 等待客户端创建互斥锁（最多等待5秒）
            Mutex clientMutex = null;
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    clientMutex = Mutex.OpenExisting(ClientAliveMutexName);
                    Console.WriteLine("Client mutex detected. Monitoring...");
                    break;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // 互斥锁还不存在，继续等待
                    await Task.Delay(100, cancellationToken);
                }
            }

            if (clientMutex == null)
            {
                Console.WriteLine("Warning: Client mutex not found within timeout. Continuing without monitoring.");
                return;
            }

            try
            {
                // 尝试获取互斥锁（不阻塞），如果能获取说明客户端已退出
                while (!cancellationToken.IsCancellationRequested)
                {
                    // WaitOne(0) 表示立即返回，不阻塞
                    if (clientMutex.WaitOne(0))
                    {
                        // 成功获取互斥锁，说明客户端已释放（退出）
                        Console.WriteLine("Client has exited. Shutting down server immediately...");
                        clientMutex.ReleaseMutex();
                        clientMutex.Dispose();
                        Stop();
                        break;
                    }

                    // 每100ms检查一次
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (AbandonedMutexException)
            {
                // 客户端异常退出，互斥锁被遗弃
                Console.WriteLine("Client crashed or terminated abnormally. Shutting down server...");
                Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client monitor error: {ex.Message}");
            }
            finally
            {
                clientMutex?.Dispose();
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
            try
            {
                // 等待监听任务结束
                _listenerTask?.Wait(100);
                Dispose();
            }
            catch { /* 忽略取消任务时的异常 */ }
        }

        private async Task ListenForRequestsAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Listening for shared memory requests...");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 1. 等待客户端的请求通知
                    // WaitOneAsync 是一个常见的模式，这里用 Task.Run 包装同步 WaitOne
                    await Task.Run(() => _requestReadySemaphore.WaitOne(), cancellationToken);

                    if (cancellationToken.IsCancellationRequested) break;

                    // 2. 从共享内存读取请求数据
                    string receivedJson = ReadFromSharedMemory(RequestBufferOffset);
                    Console.WriteLine($"Received JSON: {receivedJson}");
                    ResponseMessage response;

                    // 3. 处理请求并生成响应
                    try
                    {
                        var request = JsonSerializer.Deserialize(receivedJson, PlayerJsonContext.Default.RequestMessage);

                        if (request == null)
                        {
                            response = new ResponseMessage { Type = 0, Message = "Invalid request format." };
                        }
                        else
                        {
                            response = ExecuteCommand(request); // 同步执行命令
                        }
                    }
                    catch (JsonException jEx)
                    {
                        Console.WriteLine($"JSON deserialization error: {jEx.Message}");
                        response = new ResponseMessage { Type = 0, Message = $"JSON deserialization failed: {jEx.Message}" };
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Command processing error: {ex.Message}");
                        response = new ResponseMessage { Type = 0, Message = $"Server error: {ex.Message}" };
                    }

                    // 4. 序列化响应并写入共享内存
                    string responseJson = JsonSerializer.Serialize(response, PlayerJsonContext.Default.ResponseMessage);
                    WriteToSharedMemory(ResponseBufferOffset, responseJson);
                    Console.WriteLine($"Sent JSON response: {responseJson}");

                    // 5. 释放 Response 信号量，通知客户端可以读取响应
                    // Release(1) 确保信号量计数不超过 1
                    try { _responseReadySemaphore.Release(); }
                    catch (SemaphoreFullException) { /* 忽略，表示客户端尚未处理上一个响应 */ }
                }
                catch (OperationCanceledException)
                {
                    // 任务被 Stop() 取消
                    break;
                }
                catch (Exception ex)
                {
                    // 捕获 MMF 或信号量相关的其他错误
                    Console.WriteLine($"Shared memory communication error: {ex.Message}");
                    // 为了防止无限循环，短暂等待后继续
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        public void SendNotification(ResponseMessage notification)
        {
            try
            {
                string notificationJson = JsonSerializer.Serialize(notification, PlayerJsonContext.Default.ResponseMessage);
                WriteToSharedMemory(NotificationBufferOffset, notificationJson);
                Console.WriteLine($"Sent notification: {notificationJson}");

                // 释放通知信号量，通知客户端读取
                try { _notificationReadySemaphore.Release(); }
                catch (SemaphoreFullException)
                {
                    Console.WriteLine("Warning: Previous notification not processed by client yet.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending notification: {ex.Message}");
            }
        }

        // 辅助方法：从 MMF 读取字符串
        private string ReadFromSharedMemory(long offset)
        {
            try
            {
                // 先读取消息的长度（假设前4字节存储长度）
                int length = _accessor.ReadInt32(offset);

                if (length <= 0 || length > SharedMemoryData.MaxMessageSize - sizeof(int))
                {
                    return string.Empty; // 无效长度
                }

                byte[] buffer = new byte[length];
                // 从偏移量 offset + sizeof(int) 开始读取数据
                _accessor.ReadArray(offset + sizeof(int), buffer, 0, length);

                return Encoding.UTF8.GetString(buffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading from MMF: {ex.Message}");
                return string.Empty;
            }
        }

        // 辅助方法：将字符串写入 MMF
        private void WriteToSharedMemory(long offset, string json)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                int length = bytes.Length;

                if (length > SharedMemoryData.MaxMessageSize - sizeof(int))
                {
                    length = SharedMemoryData.MaxMessageSize - sizeof(int); // 截断
                    bytes = Encoding.UTF8.GetBytes(json[..((SharedMemoryData.MaxMessageSize - sizeof(int)) / 3)]); // 尝试按UTF8截断
                    length = bytes.Length;
                    Console.WriteLine("Warning: Message truncated due to size limit.");
                }

                // 1. 写入消息长度
                _accessor.Write(offset, length);
                // 2. 写入消息内容
                _accessor.WriteArray(offset + sizeof(int), bytes, 0, length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to MMF: {ex.Message}");
            }
        }
        private ResponseMessage ExecuteCommand(RequestMessage request)
        {
            Console.WriteLine($"Executing command: {request.Command} with data: {request.Data}");
            try
            {
                switch (request.Command)
                {
                    case "Play":
                        playBackService.PlayMusic(request.Data);
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = $"Started playing: {request.Data}",
                            Result = "Playback_Started"
                        };
                    case "PlayButton":
                        playBackService.PlayButton();
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Play button pressed.",
                            Result = "Playback_Started"
                        };
                    case "SetMusicUrl":
                        playBackService.MusicUrl = request.Data;
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = $"Music URL set to: {request.Data}",
                            Result = "MusicUrl_Set"
                        };
                    case "Volume":
                        playBackService.SetVolume(int.Parse(request.Data));
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Playback paused.",
                            Result = "Playback_Paused"
                        };                  
                    case "GetProgress":
                        var progress = playBackService.GetCurrentPosition();
                        return new ResponseMessage
                        {
                            Type = 20,
                            Message = "Current progress retrieved.",
                            Result = progress.ToString()
                        };
                    case "GetDuration":
                        var duration = playBackService.GetTotalPosition();
                        return new ResponseMessage
                        {
                            Type = 21,
                            Message = "Track duration retrieved.",
                            Result = duration.ToString()
                        };
                    case "ChangePosition":
                        playBackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(double.Parse(request.Data)));
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Playback position changed.",
                            Result = "Position_Changed"
                        };
                    case "ChangeVolume":
                        playBackService.SetVolume(double.Parse(request.Data));
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Volume changed.",
                            Result = "Volume_Changed"
                        };
                    case "UpdateSettings":
                        playBackService.UpdateSettings(request.Data);
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Settings updated.",
                            Result = "Settings_Updated"
                        };
                    case "AdjustPlaybackPosition":
                        var newpos =  playBackService.AdjustPlaybackPosition(int.Parse(request.Data));
                        return new ResponseMessage
                        {
                            Type = 22,
                            Message = "PlaybackPosition Adjusted.",
                            Result = newpos.ToString()
                        };
                    case "MusicEnd":
                        playBackService.MusicEnd();
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "MusicEnded",
                            Result = "MusicEnded"
                        };
                    case "Dispose":
                        playBackService.Dispose();
                        return new ResponseMessage
                        {
                            Type = 1000,
                            Message = "Dispose",
                            Result = "Dispose"
                        };
                    case "ToggleEqualizer":
                        playBackService.ToggleEqualizer();
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Toggled Eq",
                            Result = "Toggled_Eq"
                        };
                    case "SetEqualizer":
                        playBackService.SetEqualizer();
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Eq Setted",
                            Result = "Eq_Setted"
                        };
                    case "ClearEqualizer":
                        playBackService.ClearEqualizer();
                        return new ResponseMessage {
                            Type = 1,
                            Message = "Eq Cleared",
                            Result = "Eq_Cleared"
                        };
                    case "SetEqualizerGain":
                        var eqGain = JsonSerializer.Deserialize(request.Data, IpcEqualizerGainJsonContext.Default.IpcEqualizerGain);
                        playBackService.SetEqualizerGain(eqGain.bandIndex,eqGain.gain);
                        return new ResponseMessage { 
                            Type = 1,
                            Message = "EqGain Setted",
                            Result = "EqGain_Setted"
                        };
                    default:
                        return new ResponseMessage
                        {
                            Type = 0,
                            Message = $"Unknown command: {request.Command}",
                            Result = "Error_UnknownCommand"
                        };
                }
            }
            catch (Exception ex)
            {
                return new ResponseMessage
                {
                    Type = 0,
                    Message = $"Error during command execution: {ex.Message}",
                    Result = "Error_Execution"
                };
            }
        }

        public void PlayStateUpdate(bool isPlaying) {
            SendNotification(new ResponseMessage {
                Type = 5,
                Message = "PlayStateUpdate",
                Result = isPlaying.ToString()
            });
        }

        public void PlayBackEnded(bool isPlaying)
        {
            SendNotification(new ResponseMessage
            {
                Type = 11,
                Message = "PlayBackEnded",
                Result = isPlaying.ToString()
            });
        }

        public void Dispose()
        {
            playBackService?.Dispose();
            // 清理资源
            _cancellationTokenSource?.Cancel();
            _accessor?.Dispose();
            _mmf?.Dispose();

            // 信号量在进程终止时通常会被操作系统自动清理，但显式清理是一个好习惯
            _requestReadySemaphore?.Dispose();
            _responseReadySemaphore?.Dispose();
        }
    }
}

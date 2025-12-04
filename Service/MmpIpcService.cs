using BassPlayerSharp.Model;
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BassPlayerSharp.Service
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SharedMemoryData
    {
        public const int MaxMessageSize = 4096;
        public const int MaxResponseSize = 1024;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxMessageSize)]
        public byte[] RequestBuffer;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxResponseSize)]
        public byte[] ResponseBuffer;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxResponseSize)]
        public byte[] NotificationBuffer;

        public SharedMemoryData()
        {
            RequestBuffer = new byte[MaxMessageSize];
            ResponseBuffer = new byte[MaxResponseSize];
            NotificationBuffer = new byte[MaxResponseSize];
        }
    }

    public class MmpIpcService : IDisposable
    {
        private PlayBackService playBackService;

        private const string MmfName = "BassPlayerSharp_SharedMemory";
        private const string RequestSemaphoreName = "BassPlayerSharp_RequestReady";
        private const string ResponseSemaphoreName = "BassPlayerSharp_ResponseReady";
        private const string NotificationSemaphoreName = "BassPlayerSharp_NotificationReady";
        private const string ClientAliveMutexName = "WinUIMusicPlayer_SingleInstanceMutex";
        private const string MutexName = "BassPlayerSharp_SingleInstanceMutex";
        private static Mutex? _mutex;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;

        private Semaphore _requestReadySemaphore;
        private Semaphore _responseReadySemaphore;
        private Semaphore _notificationReadySemaphore;

        private static readonly long MmfSize = SharedMemoryData.MaxMessageSize + SharedMemoryData.MaxResponseSize * 2;
        private const long RequestBufferOffset = 0;
        private static readonly long ResponseBufferOffset = SharedMemoryData.MaxMessageSize;
        private static readonly long NotificationBufferOffset = SharedMemoryData.MaxMessageSize + SharedMemoryData.MaxResponseSize;

        private CancellationTokenSource _cancellationTokenSource;
        private Task _listenerTask;
        private Task _clientMonitorTask;

        private readonly byte[] _readBuffer;
        private readonly ArrayBufferWriter<byte> _jsonBufferWriter;

        private readonly ResponseMessage _cachedResponse = new ResponseMessage();
        private readonly ResponseMessage _cachedNotification = new ResponseMessage();

        public MmpIpcService()
        {
            CheckSingleInstance();
            _cancellationTokenSource = new CancellationTokenSource();
            // 预分配缓冲区
            _readBuffer = new byte[SharedMemoryData.MaxMessageSize];
            _jsonBufferWriter = new ArrayBufferWriter<byte>(SharedMemoryData.MaxResponseSize);
        }

        private void CheckSingleInstance()
        {
            bool mutexCreated;
            _mutex = new Mutex(true, MutexName, out mutexCreated);
            if (!mutexCreated)
            {
                Stop();
                Environment.Exit(0);
            }
        }

        public async Task StartAsync()
        {
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(MmfName, MmfSize);
                _accessor = _mmf.CreateViewAccessor(0, MmfSize);
                _requestReadySemaphore = new Semaphore(0, 1, RequestSemaphoreName, out bool requestCreatedNew);
                _responseReadySemaphore = new Semaphore(0, 1, ResponseSemaphoreName, out bool responseCreatedNew);
                _notificationReadySemaphore = new Semaphore(0, 1, NotificationSemaphoreName, out bool notificationCreatedNew);

                if (!requestCreatedNew || !responseCreatedNew)
                {
                    Console.WriteLine("Warning: Semaphores already exist. Ensure no other server is running.");
                }

                Console.WriteLine($"Server is ready for shared memory communication. MMF: {MmfName}");
                this.playBackService = new PlayBackService(this);
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
            Mutex clientMutex = null;
            Mutex selfMutex = null;
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    clientMutex = Mutex.OpenExisting(ClientAliveMutexName);
                    break;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    await Task.Delay(250, cancellationToken);
                }
            }

            if (clientMutex == null)
            {
                Console.WriteLine("Warning: Client mutex not found within timeout. Continuing without monitoring.");
                return;
            }
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (clientMutex.WaitOne(0))
                    {
                        Console.WriteLine("Client has exited. Shutting down server immediately...");
                        clientMutex.ReleaseMutex();
                        clientMutex.Dispose();
                        Stop();
                        break;
                    }
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (AbandonedMutexException)
            {
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
                _listenerTask?.Wait(100);
                Dispose();
            }
            catch { }
        }

        private async Task ListenForRequestsAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Listening for shared memory requests...");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(() => _requestReadySemaphore.WaitOne(), cancellationToken);

                    if (cancellationToken.IsCancellationRequested) break;

                    // 直接读取到预分配的缓冲区，使用Span避免string分配
                    int length = ReadFromSharedMemoryToBuffer(RequestBufferOffset, _readBuffer);

                    if (length <= 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> jsonBytes = _readBuffer.AsSpan(0, length);
                    ResponseMessage response;
                    try
                    {
                        // 使用ReadOnlySpan反序列化，避免string分配
                        var request = JsonSerializer.Deserialize(jsonBytes, PlayerJsonContext.Default.RequestMessage);

                        if (request == null)
                        {
                            response = new ResponseMessage { Type = 0, Message = "Invalid request format." };
                        }
                        else
                        {
                            response = ExecuteCommand(request);
                        }
                    }
                    catch (JsonException jEx)
                    {
                        response = new ResponseMessage { Type = 0, Message = $"JSON deserialization failed: {jEx.Message}" };
                    }
                    catch (Exception ex)
                    {
                        response = new ResponseMessage { Type = 0, Message = $"Server error: {ex.Message}" };
                    }

                    // 直接序列化到缓冲区，避免中间string分配
                    WriteResponseToSharedMemory(ResponseBufferOffset, response);
                    try { _responseReadySemaphore.Release(); }
                    catch (SemaphoreFullException) { }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        public void SendNotification(ResponseMessage notification)
        {
            try
            {
                WriteResponseToSharedMemory(NotificationBufferOffset, notification);
                try
                {
                    _notificationReadySemaphore.Release();
                }
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // 直接读取到提供的缓冲区，返回实际长度
        private int ReadFromSharedMemoryToBuffer(long offset, byte[] buffer)
        {
            try
            {
                int length = _accessor.ReadInt32(offset);

                if (length <= 0 || length > buffer.Length)
                {
                    return 0;
                }

                _accessor.ReadArray(offset + sizeof(int), buffer, 0, length);
                return length;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading from MMF: {ex.Message}");
                return 0;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // 直接从ResponseMessage序列化到共享内存，避免中间string
        private void WriteResponseToSharedMemory(long offset, ResponseMessage response)
        {
            try
            {
                // 重置writer以复用
                _jsonBufferWriter.Clear();

                // 直接序列化到ArrayBufferWriter
                using (var writer = new Utf8JsonWriter(_jsonBufferWriter, new JsonWriterOptions { SkipValidation = true }))
                {
                    JsonSerializer.Serialize(writer, response, PlayerJsonContext.Default.ResponseMessage);
                }

                ReadOnlySpan<byte> jsonBytes = _jsonBufferWriter.WrittenSpan;
                int length = jsonBytes.Length;

                // 检查大小限制
                int maxSize = (offset == NotificationBufferOffset)
                    ? SharedMemoryData.MaxResponseSize - sizeof(int)
                    : SharedMemoryData.MaxMessageSize - sizeof(int);

                if (length > maxSize)
                {
                    length = maxSize;
                    Console.WriteLine("Warning: Message truncated due to size limit.");
                }

                // 写入长度
                _accessor.Write(offset, length);

                // 使用WriteArray批量写入，避免逐字节循环
                byte[] tempArray = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    jsonBytes.Slice(0, length).CopyTo(tempArray);
                    _accessor.WriteArray(offset + sizeof(int), tempArray, 0, length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempArray);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to MMF: {ex.Message}");
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ResponseMessage ExecuteCommand(RequestMessage request)
        {
            Console.WriteLine($"Executing command: {request.Command}");
            try
            {
                //使用ReadOnlySpan<char>比较，避免string分配
                ReadOnlySpan<char> cmd = request.Command.AsSpan();

                if (cmd.SequenceEqual("Play"))
                {
                    playBackService.PlayMusic(request.Data);
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Started playing";
                    _cachedResponse.Result = "Playback_Started";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("PlayButton"))
                {
                    playBackService.PlayButton();
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Play button pressed.";
                    _cachedResponse.Result = "Playback_Started";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("SetMusicUrl"))
                {
                    playBackService.MusicUrl = request.Data;
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Music URL set";
                    _cachedResponse.Result = "MusicUrl_Set";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("Volume"))
                {
                    //使用Span解析，避免Parse的装箱
                    if (int.TryParse(request.Data.AsSpan(), out int volume))
                    {
                        playBackService.SetVolume(volume);
                    }
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Volume set.";
                    _cachedResponse.Result = "Volume_Set";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("GetProgress"))
                {
                    var progress = playBackService.GetCurrentPosition();
                    _cachedResponse.Type = MessageType.CurrentTime;
                    _cachedResponse.Message = "Current progress retrieved.";
                    _cachedResponse.Result = progress.ToString();
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("GetDuration"))
                {
                    var duration = playBackService.GetTotalPosition();
                    _cachedResponse.Type = MessageType.TotalTime;
                    _cachedResponse.Message = "Track duration retrieved.";
                    _cachedResponse.Result = duration.ToString();
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("ChangePosition"))
                {
                    if (double.TryParse(request.Data.AsSpan(), out double seconds))
                    {
                        playBackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(seconds));
                    }
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Playback position changed.";
                    _cachedResponse.Result = "Position_Changed";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("ChangeVolume"))
                {
                    if (double.TryParse(request.Data.AsSpan(), out double vol))
                    {
                        playBackService.SetVolume(vol);
                    }
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Volume changed.";
                    _cachedResponse.Result = "Volume_Changed";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("UpdateSettings"))
                {
                    playBackService.UpdateSettings(request.Data);
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Settings updated.";
                    _cachedResponse.Result = "Settings_Updated";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("AdjustPlaybackPosition"))
                {
                    if (int.TryParse(request.Data.AsSpan(), out int adjust))
                    {
                        var newpos = playBackService.AdjustPlaybackPosition(adjust);
                        _cachedResponse.Type = MessageType.PositionAdjusted;
                        _cachedResponse.Message = "PlaybackPosition Adjusted.";
                        _cachedResponse.Result = newpos.ToString();
                        return _cachedResponse;
                    }
                }
                else if (cmd.SequenceEqual("MusicEnd"))
                {
                    playBackService.MusicEnd();
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "MusicEnded";
                    _cachedResponse.Result = "MusicEnded";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("Dispose"))
                {
                    playBackService.Dispose();
                    _cachedResponse.Type = MessageType.Exit;
                    _cachedResponse.Message = "Dispose";
                    _cachedResponse.Result = "Dispose";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("ToggleEqualizer"))
                {
                    playBackService.ToggleEqualizer();
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Toggled Eq";
                    _cachedResponse.Result = "Toggled_Eq";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("SetEqualizer"))
                {
                    playBackService.SetEqualizer();
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Eq Setted";
                    _cachedResponse.Result = "Eq_Setted";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("ClearEqualizer"))
                {
                    playBackService.ClearEqualizer();
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Eq Cleared";
                    _cachedResponse.Result = "Eq_Cleared";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("SetEqualizerGain"))
                {
                    var eqGain = JsonSerializer.Deserialize(request.Data, IpcEqualizerGainJsonContext.Default.IpcEqualizerGain);
                    playBackService.SetEqualizerGain(eqGain.bandIndex, eqGain.gain);
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "EqGain Setted";
                    _cachedResponse.Result = "EqGain_Setted";
                    return _cachedResponse;
                }
                else if (cmd.SequenceEqual("UpdateEq"))
                {
                    playBackService.UpdateEqualizerFromJson(request.Data);
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Eq Updated";
                    _cachedResponse.Result = "Eq_Updated";
                    return _cachedResponse;
                } else if (cmd.SequenceEqual("FadeOut"))
                {
                    playBackService.FadeOut();
                    _cachedResponse.Type = MessageType.Success;
                    _cachedResponse.Message = "Fade Out";
                    _cachedResponse.Result = "Fade_Out";
                    return _cachedResponse;
                }

                _cachedResponse.Type = MessageType.Failed;
                _cachedResponse.Message = "Unknown command";
                _cachedResponse.Result = "Error_UnknownCommand";
                return _cachedResponse;
            }
            catch (Exception ex)
            {
                _cachedResponse.Type = MessageType.Failed;
                _cachedResponse.Message = $"Error during command execution: {ex.Message}";
                _cachedResponse.Result = "Error_Execution";
                return _cachedResponse;
            }
        }

        public void PlayStateUpdate(bool isPlaying)
        {
            _cachedNotification.Type = MessageType.PlayState;
            _cachedNotification.Message = "PlayStateUpdate";
            _cachedNotification.Result = isPlaying ? "True" : "False";
            SendNotification(_cachedNotification);
        }

        public void VolumeWriteBack(float volume)
        {
            _cachedNotification.Type = MessageType.VolumeWriteBack;
            _cachedNotification.Message = "VolumeWriteBack";
            _cachedNotification.Result = volume.ToString();
            SendNotification(_cachedNotification);
        }

        public void PlayBackEnded(bool isPlaying)
        {
            _cachedNotification.Type = MessageType.PlayEnded;
            _cachedNotification.Message = "PlayBackEnded";
            _cachedNotification.Result = isPlaying ? "True" : "False";
            SendNotification(_cachedNotification);
        }

        public void Dispose()
        {
            playBackService?.Dispose();
            _cancellationTokenSource?.Cancel();
            _accessor?.Dispose();
            _mmf?.Dispose();
            _requestReadySemaphore?.Dispose();
            _responseReadySemaphore?.Dispose();
            _notificationReadySemaphore?.Dispose();
        }
    }
}
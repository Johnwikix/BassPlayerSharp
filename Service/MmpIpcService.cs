using BassPlayerIpc.Shared;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BassPlayerSharp.Service
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SharedMemoryData
    {
        public const int MaxMessageSize = 2048;
        public const int MaxResponseSize = 512;

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
        private PlayBackService? _playBackService;

        private static readonly long MmfSize = SharedMemoryData.MaxMessageSize + SharedMemoryData.MaxResponseSize * 2;
        private const long RequestBufferOffset = 0;
        private static readonly long ResponseBufferOffset = SharedMemoryData.MaxMessageSize;
        private static readonly long NotificationBufferOffset = SharedMemoryData.MaxMessageSize + SharedMemoryData.MaxResponseSize;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;

        private Semaphore? _requestReadySemaphore;
        private Semaphore? _responseReadySemaphore;
        private Semaphore? _notificationReadySemaphore;

        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _listenerTask;
        private Task? _clientMonitorTask;

        private readonly byte[] _requestBuffer;
        private int _notificationSlot;

        // Device paging cache: first request enumerates, subsequent pages come from cache.
        private (int id, string name)[]? _cachedWasapiDevices;
        private (int id, string name)[]? _cachedAsioDevices;

        public MmpIpcService()
        {
            CheckSingleInstance();
            _cancellationTokenSource = new CancellationTokenSource();
            _requestBuffer = new byte[SharedMemoryData.MaxMessageSize];
        }

        private static void CheckSingleInstance()
        {
            _ = new Mutex(true, IpcConstants.MutexName, out bool mutexCreated);
            if (!mutexCreated)
            {
                Environment.Exit(0);
            }
        }

        public async Task StartAsync()
        {
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(IpcConstants.MmfName, MmfSize);
                _accessor = _mmf.CreateViewAccessor(0, MmfSize);
                _requestReadySemaphore = new Semaphore(0, 1, IpcConstants.RequestSemaphoreName, out _);
                _responseReadySemaphore = new Semaphore(0, 1, IpcConstants.ResponseSemaphoreName, out _);
                _notificationReadySemaphore = new Semaphore(0, 1, IpcConstants.NotificationSemaphoreName, out _);

                Console.WriteLine($"Server ready. MMF: {IpcConstants.MmfName}");
                _playBackService = new PlayBackService(this);
                _listenerTask = Task.Run(() => ListenForRequestsAsync(_cancellationTokenSource!.Token));
                _clientMonitorTask = Task.Run(() => MonitorClientAliveAsync(_cancellationTokenSource!.Token));
                await Task.WhenAny(_listenerTask, _clientMonitorTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                Dispose();
                Console.WriteLine("Server stopped.");
            }
        }

        private async Task MonitorClientAliveAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Client monitor started...");
            Mutex? clientMutex = null;
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    clientMutex = Mutex.OpenExisting(IpcConstants.ClientAliveMutexName);
                    break;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
            if (clientMutex == null)
            {
                Console.WriteLine("Warning: Client mutex not found within timeout.");
                return;
            }
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (clientMutex.WaitOne(0))
                    {
                        Console.WriteLine("Client exited. Shutting down server...");
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
                Console.WriteLine("Client crashed. Shutting down...");
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
            _cancellationTokenSource?.Cancel();
            try { _listenerTask?.Wait(100); } catch { }
            Dispose();
        }

        private async Task ListenForRequestsAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Listening for requests...");
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(() => _requestReadySemaphore!.WaitOne(), cancellationToken);
                    if (cancellationToken.IsCancellationRequested) break;

                    if (_accessor == null) continue;

                    byte sequenceId = IpcEnvelope.ReadSequenceId(_accessor, RequestBufferOffset);

                    int payloadLen = IpcEnvelope.ReadPayload(
                        _accessor, RequestBufferOffset,
                        _requestBuffer,
                        SharedMemoryData.MaxMessageSize - IpcConstants.EnvelopeHeaderSize);

                    if (payloadLen < 0)
                    {
                        WriteErrorResponse(ErrorCode.InvalidPayload, sequenceId);
                        SignalResponseReady();
                        continue;
                    }

                    var commandId = IpcEnvelope.ReadCommandId(_accessor, RequestBufferOffset);
                    HandleCommand(commandId, _requestBuffer.AsSpan(0, payloadLen), sequenceId);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { await Task.Delay(500, cancellationToken); }
            }
        }

        private void HandleCommand(CommandId commandId, ReadOnlySpan<byte> payload, byte sequenceId)
        {
            try
            {
                switch (commandId)
                {
                    case CommandId.Play:
                        {
                            var req = BinarySerializer.ReadPlayRequest(payload);
                            _playBackService!.PlayMusic(req.Url ?? string.Empty);
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.PlayButton:
                        _playBackService!.PlayButton();
                        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                        break;
                    case CommandId.SetMusicUrl:
                        {
                            var req = BinarySerializer.ReadSetMusicUrlRequest(payload);
                            _playBackService!.MusicUrl = req.Url ?? string.Empty;
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.GetTimeProgress:
                        {
                            var (curMs, totalMs) = _playBackService!.GetTimeProgress();
                            WriteTimeProgressPayload(curMs, totalMs, sequenceId);
                            break;
                        }
                    case CommandId.ChangePosition:
                        {
                            var req = BinarySerializer.ReadChangePositionRequest(payload);
                            _playBackService!.ChangeWaveChannelTime(req.PositionMs);
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.ChangeVolume:
                        {
                            var req = BinarySerializer.ReadChangeVolumeRequest(payload);
                            _playBackService!.SetVolume(req.Volume);
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.MusicEnd:
                        _playBackService!.MusicEnd();
                        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                        break;
                    case CommandId.FadeOut:
                        _playBackService!.FadeOut();
                        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                        break;
                    case CommandId.UpdateSettings:
                        {
                            var settings = BinarySerializer.ReadIpcSetting(payload);
                            _playBackService!.UpdateSettings(settings);
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.AdjustPlaybackPosition:
                        {
                            var req = BinarySerializer.ReadAdjustPlaybackPositionRequest(payload);
                            var newPos = _playBackService!.AdjustPlaybackPosition(req.CurMs, req.TotalMs, req.DeltaMs);
                            WritePositionResponsePayload(MessageTypeId.PositionAdjusted, newPos, sequenceId);
                            break;
                        }
                    case CommandId.ToggleEqualizer:
                        _playBackService!.ToggleEqualizer();
                        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                        break;
                    case CommandId.SetEqualizer:
                        _playBackService!.SetEqualizer();
                        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                        break;
                    case CommandId.ClearEqualizer:
                        _playBackService!.ClearEqualizer();
                        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                        break;
                    case CommandId.SetEqualizerGain:
                        {
                            var req = BinarySerializer.ReadSetEqualizerGainRequest(payload);
                            _playBackService!.SetEqualizerGain(req.BandIndex, req.Gain);
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.UpdateEq:
                        {
                            var req = BinarySerializer.ReadUpdateEqRequest(payload);
                            _playBackService!.UpdateEqualizer(req);
                            WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                            break;
                        }
                    case CommandId.GetWasapiDevices:
                        HandleGetDevices(MessageTypeId.WasapiDevices, ref _cachedWasapiDevices,
                            () => _playBackService!.GetWasapiDevices(), payload, sequenceId);
                        break;
                    case CommandId.GetAsioDevices:
                        HandleGetDevices(MessageTypeId.AsioDevices, ref _cachedAsioDevices,
                            () => _playBackService!.GetAsioDevices(), payload, sequenceId);
                        break;
                    default:
                        WriteErrorResponse(ErrorCode.InvalidCommand, sequenceId);
                        break;
                }
            }
            catch (Exception)
            {
                WriteErrorResponse(ErrorCode.Unknown, sequenceId);
            }
            SignalResponseReady();
        }

        private void HandleGetDevices(
            MessageTypeId typeId,
            ref (int id, string name)[]? cache,
            Func<(int id, string name)[]> enumerate,
            ReadOnlySpan<byte> payload,
            byte sequenceId)
        {
            var req = BinarySerializer.ReadGetDevicesRequest(payload);
            cache ??= enumerate();
            var devices = cache;

            int total = devices.Length;
            int perPage = MaxDevicesPerResponse();
            int totalPages = total == 0 ? 1 : (total + perPage - 1) / perPage;
            if (req.Page >= totalPages) req.Page = 0;

            int start = req.Page * perPage;
            int end = Math.Min(start + perPage, total);
            int count = end - start;

            int maxResp = SharedMemoryData.MaxResponseSize - IpcConstants.EnvelopeHeaderSize;
            Span<byte> buf = stackalloc byte[maxResp];
            int offset = BinarySerializer.WriteDeviceListPageHeader(buf, req.Page, (byte)totalPages, (byte)count);
            for (int i = start; i < end; i++)
            {
                var span = buf[offset..];
                offset += BinarySerializer.WriteDeviceEntry(span, devices[i].id, devices[i].name);
            }
            IpcEnvelope.WriteResponse(_accessor!, ResponseBufferOffset, typeId, sequenceId, buf[..offset], SharedMemoryData.MaxResponseSize);
        }

        private static int MaxDevicesPerResponse()
        {
            int maxPayload = SharedMemoryData.MaxResponseSize - IpcConstants.EnvelopeHeaderSize;
            int perEntry = BinarySerializer.MaxDeviceEntrySize(64); // assume max 64-byte names
            int afterHeader = maxPayload - BinarySerializer.DeviceListPageHeaderSize;
            return Math.Max(1, afterHeader / perEntry);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteErrorResponse(ErrorCode code, byte sequenceId)
        {
            Span<byte> buf = stackalloc byte[BinarySerializer.FailedResponseSize];
            var resp = new FailedResponse { Code = code };
            BinarySerializer.WriteFailedResponse(buf, resp);
            IpcEnvelope.WriteResponse(_accessor!, ResponseBufferOffset, MessageTypeId.Failed, sequenceId, buf, SharedMemoryData.MaxResponseSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteEmptyResponse(MessageTypeId typeId, byte sequenceId)
        {
            IpcEnvelope.WriteResponse(_accessor!, ResponseBufferOffset, typeId, sequenceId, ReadOnlySpan<byte>.Empty, SharedMemoryData.MaxResponseSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteTimeProgressPayload(long currentMs, long totalMs, byte sequenceId)
        {
            Span<byte> buf = stackalloc byte[BinarySerializer.TimeProgressSize];
            BinarySerializer.WriteTimeProgress(buf, currentMs, totalMs);
            IpcEnvelope.WriteResponse(_accessor!, ResponseBufferOffset, MessageTypeId.TimeProgress, sequenceId, buf, SharedMemoryData.MaxResponseSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WritePositionResponsePayload(MessageTypeId typeId, long positionMs, byte sequenceId)
        {
            Span<byte> buf = stackalloc byte[BinarySerializer.PositionResponseSize];
            var resp = new PositionResponse { PositionMs = positionMs };
            BinarySerializer.WritePositionResponse(buf, resp);
            IpcEnvelope.WriteResponse(_accessor!, ResponseBufferOffset, typeId, sequenceId, buf, SharedMemoryData.MaxResponseSize);
        }

        private void SignalResponseReady()
        {
            try { _responseReadySemaphore!.Release(); }
            catch (SemaphoreFullException) { }
        }

        public void SendNotification(MessageTypeId typeId, scoped ReadOnlySpan<byte> payload)
        {
            if (_accessor == null) return;
            try
            {
                Interlocked.Increment(ref _notificationSlot);
                long offset = NotificationBufferOffset;
                IpcEnvelope.WriteResponse(_accessor, offset, typeId, 0, payload, SharedMemoryData.MaxResponseSize);
                try { _notificationReadySemaphore!.Release(); }
                catch (SemaphoreFullException)
                {
                    Interlocked.Increment(ref _notificationSlot);
                    IpcEnvelope.WriteResponse(_accessor, offset, MessageTypeId.NotificationDropped, 0, ReadOnlySpan<byte>.Empty, SharedMemoryData.MaxResponseSize);
                    try { _notificationReadySemaphore!.Release(); } catch { }
                }
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PlayStateUpdate(bool isPlaying)
        {
            Span<byte> buf = stackalloc byte[BinarySerializer.PlayStateResponseSize];
            var resp = new PlayStateResponse { IsPlaying = isPlaying };
            BinarySerializer.WritePlayStateResponse(buf, resp);
            SendNotification(MessageTypeId.PlayState, buf);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void VolumeWriteBack(float volume)
        {
            Span<byte> buf = stackalloc byte[BinarySerializer.VolumeResponseSize];
            var resp = new VolumeResponse { Volume = volume };
            BinarySerializer.WriteVolumeResponse(buf, resp);
            SendNotification(MessageTypeId.VolumeWriteBack, buf);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PlayBackEnded(bool isPlaying)
        {
            SendNotification(MessageTypeId.PlayEnded, ReadOnlySpan<byte>.Empty);
        }

        public void Dispose()
        {
            _playBackService?.Dispose();
            _cancellationTokenSource?.Cancel();
            _accessor?.Dispose();
            _mmf?.Dispose();
            _requestReadySemaphore?.Dispose();
            _responseReadySemaphore?.Dispose();
            _notificationReadySemaphore?.Dispose();
        }
    }
}

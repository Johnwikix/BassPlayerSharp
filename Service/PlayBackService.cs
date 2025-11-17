using BassPlayerSharp.Manager;
using BassPlayerSharp.Model;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Dsd;
using ManagedBass.Fx;
using ManagedBass.Wasapi;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BassPlayerSharp.Service
{
    public class PlayBackService
    {
        private readonly MmpIpcService _mmpIpcService;
        public int _currentStream;
        private readonly SyncProcedure _syncEndCallback;
        private readonly SyncProcedure _syncFailCallback;
        private readonly WasapiProcedure _myWasapiProcedure;
        private readonly AsioProcedure _myAsioProcedure;
        public int? lastPlayedMusicId;
        public bool isPausing = false;
        public bool isSettingsChangeStop = false;
        public float volume = 0.5f;
        public bool isInitializing = true;
        private readonly Lock _streamLock = new();
        private readonly Lock _waveChannelLock = new();
        private readonly int[] _bandIndices = new int[10];
        private readonly float[] _eqFrequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        private double MinDb = -60;
        private double MaxDb = 0;
        private double MiddleDb = -30;
        private PeakEQ _peakEQ;
        public bool IsPlaying = false;
        public string OutputMode = "DirectSound";
        public int BassOutputDeviceId = -1;
        public int BassASIODeviceId = 0;
        public int Latency = 400;
        public bool IsDopEnabled = false;
        public string MusicUrl;
        public int dsdGain = 6;
        public int dsdPcmFreq = 88200;
        public bool IsEqualizerEnabled = false;
        private bool IsVolumeSafety = false;
        private bool IsFadingEnabled = false;
        private Timer _fadeTimer;
        private int _currentStep;
        private readonly int _totalSteps = 50;
        private float _volumeStep;
        private float _curve;
        private float _startVolume;
        private float _targetVolume;
        private bool _isFading;

        // 预分配字符串常量，避免重复分配
        private static readonly string DsfExtension = ".dsf";
        private static readonly string DffExtension = ".dff";

        // 使用 ArrayPool 复用数组
        private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

        // 缓存扩展名比较器，避免每次都创建
        private static readonly StringComparison OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase;

        // 静态字典避免装箱
        public static readonly Dictionary<string, double> equalizer = new()
        {
            {"32Hz", 0}, {"64Hz", 0}, {"125Hz", 0}, {"250Hz", 0}, {"500Hz", 0},
            {"1kHz", 0}, {"2kHz", 0}, {"4kHz", 0}, {"8kHz", 0}, {"16kHz", 0}
        };

        public static readonly Dictionary<float, string> FloatToString = new()
        {
            [32f] = "32Hz",
            [64f] = "64Hz",
            [125f] = "125Hz",
            [250f] = "250Hz",
            [500f] = "500Hz",
            [1000f] = "1kHz",
            [2000f] = "2kHz",
            [4000f] = "4kHz",
            [8000f] = "8kHz",
            [16000f] = "16kHz"
        };

        // 缓存 ChannelInfo 避免重复分配
        private ChannelInfo _cachedChannelInfo;

        public PlayBackService(MmpIpcService mmpIpcService)
        {
            _mmpIpcService = mmpIpcService;
            BassManager.Initialize();
            _syncEndCallback = OnPlayBackEnded;
            _syncFailCallback = OnPlaybackFailed;
            _myWasapiProcedure = OnWasapiProc;
            _myAsioProcedure = OnAsioProc;
            _fadeTimer = new Timer(OnFadeTimer, null, Timeout.Infinite, Timeout.Infinite);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FadeIn(float targetVolume, int durationMs = 500)
        {
            StopFade();
            _currentStep = 0;
            _startVolume = 0f;
            _targetVolume = targetVolume;
            _isFading = true;

            int intervalMs = durationMs / _totalSteps;
            Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, 0f);
            _fadeTimer.Change(0, intervalMs);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FadeOut(int durationMs = 1000)
        {
            StopFade();
            _currentStep = 0;
            _targetVolume = 0f;
            Bass.ChannelGetAttribute(_currentStream, ChannelAttribute.Volume,out _startVolume);
            _isFading = true;

            int intervalMs = durationMs / _totalSteps;
            _fadeTimer.Change(0, intervalMs);
        }

        public void StopFade()
        {
            if (_isFading)
            {
                _fadeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _isFading = false;
            }
        }

        private void OnFadeTimer(object state)
        {
            if (!_isFading || _currentStep > _totalSteps)
            {
                StopFade();
                return;
            }

            // 使用指数曲线计算音量（对数感知）
            // t: 0.0 到 1.0 的进度
            _volumeStep = (float)_currentStep / _totalSteps;

            // 使用平方根曲线（淡入）或平方曲线（淡出）
            
            if (_targetVolume > _startVolume)
            {
                // 淡入：使用平方曲线，开始慢后面快
                _curve = _volumeStep * _volumeStep;
            }
            else
            {
                // 淡出：使用平方根曲线，开始快后面慢
                _curve = (float)Math.Sqrt(_volumeStep);
            }

            float volume = _startVolume + (_targetVolume - _startVolume) * _curve;

            // 确保音量在有效范围内
            if (volume < 0f) volume = 0f;
            if (volume > 1f) volume = 1f;

            Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);

            _currentStep++;

            if (_currentStep > _totalSteps)
            {
                StopFade();
            }
        }


        private void OnPlaybackFailed(int Handle, int Channel, int Data, nint User)
        {
            IsPlaying = false;
        }
        private void OnPlayBackEnded(int Handle, int Channel, int Data, nint User)
        {
            IsPlaying = false;
            _mmpIpcService.PlayBackEnded(IsPlaying);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int OnWasapiProc(IntPtr buffer, int length, IntPtr user)
        {
            return _currentStream != 0 ? Bass.ChannelGetData(_currentStream, buffer, length) : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int OnAsioProc(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            return _currentStream != 0 ? Bass.ChannelGetData(user.ToInt32(), buffer, length) : 0;
        }

        public void MusicEnd()
        {
            if (_currentStream != 0)
            {
                if (OutputMode.Contains("Wasapi"))
                {
                    BassWasapi.Stop();
                }
                else
                {
                    Bass.ChannelStop(_currentStream);
                }
                ChangeWaveChannelTime(TimeSpan.Zero);
            }
            IsPlaying = false;
        }
        public void UpdateEqualizerFromJson(string equalizerJson)
        {
            // 1. 转换为字节数组（有分配）
            var bytes = System.Text.Encoding.UTF8.GetBytes(equalizerJson);

            // 2. 创建 Reader（栈上分配，无堆分配）
            var reader = new System.Text.Json.Utf8JsonReader(bytes);

            // 3. 逐个读取 JSON token
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var key = reader.GetString();  // "32Hz"
                    reader.Read();                 // 移动到值

                    if (reader.TokenType == JsonTokenType.Number &&
                        equalizer.ContainsKey(key))
                    {
                        equalizer[key] = reader.GetDouble();  // -2
                    }
                }
            }
        }
        public void ToggleEqualizer()
        {
            if (!IsEqualizerEnabled) return;

            if (IsDopEnabled && (OutputMode.Contains("WasapiExclusive") || OutputMode == "ASIO")
                && IsDsdFile(MusicUrl))
            {
                return;
            }

            try
            {
                if (_currentStream != 0)
                {
                    _peakEQ = new PeakEQ(_currentStream, Q: 0, Bandwith: 1.0);
                    for (int i = 0; i < _eqFrequencies.Length; i++)
                    {
                        _bandIndices[i] = _peakEQ.AddBand(_eqFrequencies[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化均衡器时出错: {ex.Message}");
                _peakEQ = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetEqualizerGain(int bandIndex, float gain)
        {
            if (bandIndex < 0 || bandIndex >= _eqFrequencies.Length || _peakEQ == null)
                return;

            try
            {
                _peakEQ.UpdateBand(_bandIndices[bandIndex], gain);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置均衡器参数失败: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetEqualizer()
        {
            if (_peakEQ == null) return;

            for (int i = 0; i < 10; i++)
            {
                _peakEQ.UpdateBand(_bandIndices[i], (float)equalizer[FloatToString[_eqFrequencies[i]]]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearEqualizer()
        {
            DisposeEq();
        }

        // 优化：缓存文件扩展名检查结果，避免重复 Path.GetExtension 调用
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsDsdFile(ReadOnlySpan<char> path)
        {
            if (path.Length < 4) return false;

            var ext = path.Slice(path.Length - 4);
            return ext.Equals(DsfExtension, OrdinalIgnoreCase) ||
                   ext.Equals(DffExtension, OrdinalIgnoreCase);
        }

        private bool SwitchDevice(ChannelInfo channelInfo)
        {
            bool result = false;
            switch (OutputMode)
            {
                case "WasapiShared":
                    result = BassWasapi.Init(BassOutputDeviceId, channelInfo.Frequency, channelInfo.Channels,
                        WasapiInitFlags.Shared, Latency / 1000.0f, 0, _myWasapiProcedure);
                    break;
                case "WasapiExclusivePush":
                    result = BassWasapi.Init(BassOutputDeviceId, channelInfo.Frequency, channelInfo.Channels,
                        WasapiInitFlags.Exclusive, Latency / 1000.0f, Latency / 8000.0f, _myWasapiProcedure);
                    break;
                case "WasapiExclusiveEvent":
                    result = BassWasapi.Init(BassOutputDeviceId, channelInfo.Frequency, channelInfo.Channels,
                        WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven,
                        Latency / 1000.0f, Latency / 8000.0f, _myWasapiProcedure);
                    break;
                case "ASIO":
                    result = BassAsio.Init(BassASIODeviceId, AsioInitFlags.Thread);
                    break;
            }

            if (OutputMode.Contains("Wasapi"))
            {
                BassWasapi.GetInfo(out var info);
                MaxDb = info.MaxVolume;
                MinDb = info.MinVolume;
                MiddleDb = (MinDb + MaxDb) / 2;
            }
            return result;
        }

        private bool InitializePlayback()
        {
            try
            {
                Bass.ChannelGetInfo(_currentStream, out _cachedChannelInfo);
                var result = SwitchDevice(_cachedChannelInfo);

                if (!result)
                {
                    StopWasapiPlayback();
                    StopAsioPlayback();
                    result = SwitchDevice(_cachedChannelInfo);
                    if (!result) return false;
                }

                switch (OutputMode)
                {
                    case "WasapiShared":
                        BassWasapi.SetVolume(WasapiVolumeTypes.Session, volume);
                        break;
                    case "WasapiExclusivePush":
                    case "WasapiExclusiveEvent":
                        if (IsVolumeSafety)
                        {
                            volume = (float)DbToLinear(MiddleDb);
                            BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)MiddleDb);
                            _mmpIpcService.VolumeWriteBack(volume);
                            IsVolumeSafety = false;
                        }
                        else
                        {
                            BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
                        }
                        break;
                    case "ASIO":
                        if (IsDopEnabled && IsDsdFile(MusicUrl))
                        {
                            Bass.ChannelGetAttribute(_currentStream, ChannelAttribute.DSDRate, out float dsdRate);
                            if (!BassAsio.SetDSD(true)) return false;
                            BassAsio.Rate = dsdRate;
                            if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.DSD_MSB)) return false;
                            if (!BassAsio.ChannelEnable(false, 0, _myAsioProcedure, new IntPtr(_currentStream))) return false;
                            if (!BassAsio.ChannelJoin(false, 1, 0)) return false;
                        }
                        else
                        {
                            if (!BassAsio.ChannelEnableBass(false, 0, _currentStream, true)) return false;
                            if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float)) return false;
                            BassAsio.Rate = _cachedChannelInfo.Frequency;
                        }
                        BassAsio.ChannelSetVolume(false, -1, volume);
                        break;
                }

                Debug.WriteLine("播放模式启动成功");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动播放模式时出错: {ex}");
                return false;
            }
        }

        private void SetSource(string musicUrl)
        {
            try
            {
                DisposeStream();
                BassDsd.DefaultGain = dsdGain;
                BassDsd.DefaultFrequency = dsdPcmFreq;
                var isDsd = IsDsdFile(musicUrl);
                _currentStream = (OutputMode, isDsd, IsDopEnabled) switch
                {
                    ("WasapiExclusivePush" or "WasapiExclusiveEvent", true, true) =>
                        BassDsd.CreateStream(musicUrl, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode | BassFlags.AsyncFile),
                    ("WasapiExclusivePush" or "WasapiExclusiveEvent", _, _) =>
                        Bass.CreateStream(musicUrl, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode),
                    ("WasapiShared", _, _) =>
                        Bass.CreateStream(musicUrl, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode),
                    ("ASIO", true, true) =>
                        BassDsd.CreateStream(musicUrl, 0, 0, BassFlags.DSDRaw | BassFlags.Decode | BassFlags.AsyncFile),
                    ("ASIO", _, _) =>
                        Bass.CreateStream(musicUrl, 0, 0, BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode),
                    _ =>
                        Bass.CreateStream(musicUrl, 0, 0, BassFlags.Default | BassFlags.AsyncFile)
                };

                if (_currentStream == 0) return;

                Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _syncEndCallback);
                Bass.ChannelSetSync(_currentStream, SyncFlags.Stalled, 0, _syncFailCallback);
                ToggleEqualizer();

                if (!OutputMode.Contains("Wasapi") && OutputMode != "ASIO")
                {
                    Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetSource异常: {ex.Message}");
            }
        }

        public void PlayMusic(string musicUrl, bool isSettingChanged = false)
        {
            lock (_streamLock)
            {
                MusicUrl = musicUrl;
                if (IsFadingEnabled && IsPlaying && OutputMode == "DirectSound" && _currentStream != 0)
                {
                    MusicFadeOut(MusicUrl, isSettingChanged);
                }
                else
                {
                    Stop();
                    SetSource(MusicUrl);
                    Play(isSettingChanged);
                }
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async void MusicFadeOut(string newMusicUrl, bool isSettingChanged)
        {
            // 检查是否临近歌曲结束（最后3秒）
            double currentPos = GetCurrentPosition();
            double totalPos = GetTotalPosition();
            double remainingTime = totalPos - currentPos;

            // 如果剩余时间小于3秒或歌曲时长无效，直接切换不淡出
            if (remainingTime < 3 || totalPos <= 0)
            {
                Stop();
                SetSource(newMusicUrl);
                Play(isSettingChanged);
                return;
            }

            // 计算淡出时长：取剩余时间和1秒中的较小值
            int fadeOutDuration = (int)Math.Min(remainingTime * 500, 500);
            // 启动淡出
            FadeOut(fadeOutDuration);
            // 使用Timer在淡出完成后切换到新歌曲
            await Task.Delay(fadeOutDuration + 50);
            lock (_streamLock)
            {
                StopFade();
                Stop();
                SetSource(newMusicUrl);
                Play(isSettingChanged);
            }
        }

        public void Stop()
        {
            if (_currentStream != 0)
            {
                Bass.ChannelStop(_currentStream);
            }
        }

        public async void PlayButton()
        {
            if (IsPlaying)
            {
                switch (OutputMode)
                {
                    case var mode when mode.Contains("Wasapi"):
                        BassWasapi.Stop();
                        break;
                    case "ASIO":
                        BassAsio.Stop();
                        break;
                    default:
                        if (IsFadingEnabled)
                        {
                            FadeOut();
                            await Task.Delay(550);
                            Bass.ChannelStop(_currentStream);
                        }
                        else {
                            Bass.ChannelStop(_currentStream);
                        }                        
                        break;
                }
                isPausing = true;
                IsPlaying = false;
            }
            else
            {
                if (_currentStream != 0)
                {
                    switch (OutputMode)
                    {
                        case var mode when mode.Contains("Wasapi"):
                            BassWasapi.Start();
                            break;
                        case "ASIO":
                            BassAsio.Start();
                            break;
                        default:
                            if (IsFadingEnabled) {
                                FadeIn(volume);
                            }
                            Bass.ChannelPlay(_currentStream, false);
                            break;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(MusicUrl))
                {
                    PlayMusic(MusicUrl);
                }
                isPausing = false;
                IsPlaying = true;
            }
            _mmpIpcService.PlayStateUpdate(IsPlaying);
        }

        public void Play(bool isSettingChanged = false)
        {
            if (_currentStream == 0) return;

            bool success = OutputMode switch
            {
                var mode when mode.Contains("Wasapi") => InitializePlayback() && TryStart(() => BassWasapi.Start()),
                "ASIO" => InitializePlayback() && TryStart(() => BassAsio.Start()),
                _ => TryStart(() => { 
                    Bass.ChannelPlay(_currentStream, false);
                    if (IsFadingEnabled)
                    {
                        FadeIn(volume);
                    }
                })
            };

            if (!success)
            {
                Bass.ChannelPlay(_currentStream, false);
                if (IsFadingEnabled) {
                    FadeIn(volume);
                }
            }

            if (IsEqualizerEnabled)
            {
                SetEqualizer();
            }

            IsPlaying = true;
            _mmpIpcService.PlayStateUpdate(IsPlaying);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryStart(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ChangeWaveChannelTime(TimeSpan timeSpan)
        {
            lock (_waveChannelLock)
            {
                if (_currentStream != 0)
                {
                    var targetBytes = Bass.ChannelSeconds2Bytes(_currentStream, timeSpan.TotalSeconds);
                    Bass.ChannelSetPosition(_currentStream, targetBytes);
                }
            }
        }

        public void UpdateSettings(string settings)
        {
            var ipcSetting = System.Text.Json.JsonSerializer.Deserialize(settings, IpcSettingJsonContext.Default.IpcSetting);
            if (ipcSetting == null) return;
            OutputMode = ipcSetting.OutputMode;
            BassOutputDeviceId = ipcSetting.BassOutputDeviceId;
            BassASIODeviceId = ipcSetting.BassASIODeviceId;
            Latency = ipcSetting.Latency;
            IsDopEnabled = ipcSetting.IsDopEnabled;
            dsdGain = ipcSetting.dsdGain;
            dsdPcmFreq = ipcSetting.dsdPcmFreq;
            IsEqualizerEnabled = ipcSetting.IsEqualizerEnabled;
            volume = ipcSetting.Volume;
            IsFadingEnabled = ipcSetting.IsFadeEnabled;
            if (ipcSetting.IsSettingChanged)
            {
                ChangingSetting();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVolume(double volume)
        {
            this.volume = (float)volume;
            if (_currentStream == 0) return;

            switch (OutputMode)
            {
                case "WasapiExclusivePush":
                case "WasapiExclusiveEvent":
                    BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
                    break;
                case "WasapiShared":
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                    break;
                case "ASIO":
                    BassAsio.ChannelSetVolume(false, -1, volume);
                    break;
                default:
                    Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetCurrentPosition()
        {
            if (_currentStream == 0) return 0;
            var positionBytes = Bass.ChannelGetPosition(_currentStream);
            return Bass.ChannelBytes2Seconds(_currentStream, positionBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetTotalPosition()
        {
            if (_currentStream == 0) return 0;
            var totalBytes = Bass.ChannelGetLength(_currentStream);
            return Bass.ChannelBytes2Seconds(_currentStream, totalBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double AdjustPlaybackPosition(int seconds)
        {
            if (!IsPlaying || _currentStream == 0) return 0;

            double newPosition = GetCurrentPosition() + seconds;
            newPosition = Math.Clamp(newPosition, 0, GetTotalPosition());
            ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
            return newPosition;
        }

        public void ChangingSetting()
        {
            try
            {
                lock (_streamLock)
                {
                    var currentTime = GetCurrentPosition();
                    if (OutputMode.Contains("WasapiExclusive"))
                    {
                        IsVolumeSafety = true;
                    }
                    if (IsPlaying)
                    {
                        Stop();
                        SetSource(MusicUrl);
                        Play(true);
                    }
                    else
                    {
                        SetSource(MusicUrl);
                    }
                    ChangeWaveChannelTime(TimeSpan.FromSeconds(currentTime));
                }
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double LinearToDb(double linearValue)
        {
            if (linearValue <= 0) return MinDb;
            if (linearValue >= 1) return MaxDb;

            return MaxDb + (MinDb - MaxDb) * (1 - Math.Log10(9 * linearValue + 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double DbToLinear(double dbValue)
        {
            dbValue = Math.Clamp(dbValue, MinDb, MaxDb);
            if (dbValue <= MinDb) return 0;

            double dbPosition = (dbValue - MaxDb) / (MinDb - MaxDb);
            return (Math.Pow(10, (1 - dbPosition)) - 1) / 9;
        }

        private void DisposeStream()
        {
            if (_currentStream != 0)
            {
                Bass.StreamFree(_currentStream);
                _currentStream = 0;
            }
            StopWasapiPlayback();
            StopAsioPlayback();
            DisposeEq();
        }

        private void StopWasapiPlayback()
        {
            try
            {
                if (BassWasapi.IsStarted)
                {
                    BassWasapi.Stop(true);
                }
                BassWasapi.Free();
                IsPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止WASAPI播放时出错: {ex}");
            }
        }

        private void StopAsioPlayback()
        {
            try
            {
                if (BassAsio.IsStarted)
                {
                    BassAsio.Stop();
                }
                BassAsio.Free();
                IsPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止ASIO播放时出错: {ex}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DisposeEq()
        {
            _peakEQ?.Dispose();
            _peakEQ = null;
        }

        public void Dispose()
        {
            DisposeEq();
            DisposeStream();
            _fadeTimer?.Dispose();
            BassManager.Free();
        }
    }
}
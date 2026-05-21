using BassPlayerSharp.Manager;
using BassPlayerIpc.Shared;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Dsd;
using ManagedBass.Fx;
using ManagedBass.Wasapi;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BassPlayerSharp.Service
{
    public class PlayBackService
    {
        private readonly MmpIpcService _mmpIpcService;
        public int _currentStream;
        public int _dsdFlagTempStream;
        public ChannelInfo _dsdFlagTempChannelInfo;
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
        private PeakEQ? _peakEQ;
        public bool IsPlaying = false;
        public string OutputMode = "DirectSound";
        public int BassOutputDeviceId = -1;
        public int BassASIODeviceId = 0;
        public int Latency = 400;
        public bool IsDopEnabled = false;
        public string? MusicUrl;
        public int dsdGain = 6;
        public int dsdPcmFreq = 88200;
        public bool IsEqualizerEnabled = false;
        private bool IsFadingEnabled = false;
        private Timer? _fadeTimer;
        private int _currentStep;
        private readonly int _totalSteps = 50;
        private float _volumeStep;
        private float _curve;
        private float _startVolume;
        private float _targetVolume;
        private bool _isFading;

        private static readonly string DsfExtension = ".dsf";
        private static readonly string DffExtension = ".dff";
        private static readonly string WvExtension = ".wv";
        private static readonly StringComparison OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase;

        public readonly Dictionary<float, string> FloatToString = new()
        {
            [32f] = "32Hz", [64f] = "64Hz", [125f] = "125Hz",
            [250f] = "250Hz", [500f] = "500Hz",
            [1000f] = "1kHz", [2000f] = "2kHz",
            [4000f] = "4kHz", [8000f] = "8kHz", [16000f] = "16kHz"
        };

        public readonly float[] EqGains = new float[10];

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
            _fadeTimer!.Change(0, intervalMs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FadeOut(int durationMs = 500)
        {
            StopFade();
            _currentStep = 0;
            _targetVolume = 0f;
            Bass.ChannelGetAttribute(_currentStream, ChannelAttribute.Volume, out _startVolume);
            _isFading = true;
            int intervalMs = durationMs / _totalSteps;
            _fadeTimer!.Change(0, intervalMs);
        }

        public void StopFade()
        {
            if (_isFading)
            {
                _fadeTimer!.Change(Timeout.Infinite, Timeout.Infinite);
                _isFading = false;
            }
        }

        private void OnFadeTimer(object? state)
        {
            if (!_isFading || _currentStep > _totalSteps) { StopFade(); return; }
            _volumeStep = (float)_currentStep / _totalSteps;
            _curve = _targetVolume > _startVolume
                ? _volumeStep * _volumeStep
                : (float)Math.Sqrt(_volumeStep);
            float vol = _startVolume + (_targetVolume - _startVolume) * _curve;
            if (vol < 0f) vol = 0f;
            if (vol > 1f) vol = 1f;
            Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, vol);
            _currentStep++;
            if (_currentStep > _totalSteps) StopFade();
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
                    BassWasapi.Stop();
                else if (OutputMode.Contains("ASIO"))
                    BassAsio.Stop();
                else
                    Bass.ChannelStop(_currentStream);
                ChangeWaveChannelTime(TimeSpan.Zero);
            }
            IsPlaying = false;
        }

        public void UpdateEqualizer(UpdateEqRequest eq)
        {
            EqGains[0] = eq.Band0;
            EqGains[1] = eq.Band1;
            EqGains[2] = eq.Band2;
            EqGains[3] = eq.Band3;
            EqGains[4] = eq.Band4;
            EqGains[5] = eq.Band5;
            EqGains[6] = eq.Band6;
            EqGains[7] = eq.Band7;
            EqGains[8] = eq.Band8;
            EqGains[9] = eq.Band9;
        }

        public void ToggleEqualizer()
        {
            if (!IsEqualizerEnabled) return;
            if (IsDopEnabled && (OutputMode.Contains("WasapiExclusive") || OutputMode == "ASIO")
                && IsDsdFile(MusicUrl)) return;
            try
            {
                if (_currentStream != 0)
                {
                    _peakEQ = new PeakEQ(_currentStream, Q: 0, Bandwith: 1.0);
                    for (int i = 0; i < _eqFrequencies.Length; i++)
                        _bandIndices[i] = _peakEQ.AddBand(_eqFrequencies[i]);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Init EQ error: {ex.Message}");
                _peakEQ = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetEqualizerGain(byte bandIndex, float gain)
        {
            if (bandIndex >= _eqFrequencies.Length || _peakEQ == null)
                return;
            try
            {
                _peakEQ.UpdateBand(_bandIndices[bandIndex], gain);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Set EQ band error: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetEqualizer()
        {
            if (_peakEQ == null) return;
            for (int i = 0; i < 10; i++)
                _peakEQ.UpdateBand(_bandIndices[i], EqGains[i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearEqualizer()
        {
            _peakEQ?.Dispose();
            _peakEQ = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsDsdFile(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4) return false;
            if (Path.GetExtension(path).Equals(WvExtension, OrdinalIgnoreCase))
            {
                try
                {
                    _dsdFlagTempStream = BassDsd.CreateStream(path, 0, 0, BassFlags.DSDRaw | BassFlags.Decode | BassFlags.AsyncFile);
                    Bass.ChannelGetInfo(_dsdFlagTempStream, out _dsdFlagTempChannelInfo);
                    if (_dsdFlagTempChannelInfo.Frequency >= 352800 && _dsdFlagTempChannelInfo.OriginalResolution == 0
                        && _dsdFlagTempChannelInfo.ChannelType == ChannelType.WV)
                        return true;
                    return false;
                }
                finally { Bass.StreamFree(_dsdFlagTempStream); }
            }
            return Path.GetExtension(path).Equals(DsfExtension, OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(DffExtension, OrdinalIgnoreCase);
        }

        private bool SwitchDevice(ChannelInfo channelInfo)
        {
            return OutputMode switch
            {
                "WasapiShared" => BassWasapi.Init(BassOutputDeviceId, channelInfo.Frequency, channelInfo.Channels,
                    WasapiInitFlags.Shared, Latency / 1000.0f, 0, _myWasapiProcedure),
                "WasapiExclusivePush" => BassWasapi.Init(BassOutputDeviceId, channelInfo.Frequency, channelInfo.Channels,
                    WasapiInitFlags.Exclusive, Latency / 1000.0f, Latency / 8000.0f, _myWasapiProcedure),
                "WasapiExclusiveEvent" => BassWasapi.Init(BassOutputDeviceId, channelInfo.Frequency, channelInfo.Channels,
                    WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven,
                    Latency / 1000.0f, Latency / 8000.0f, _myWasapiProcedure),
                "ASIO" => BassAsio.Init(BassASIODeviceId, AsioInitFlags.Thread),
                _ => false
            };
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
                        BassWasapi.SetVolume(WasapiVolumeTypes.WindowsHybridCurve, volume);
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
                Debug.WriteLine("Playback init success");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Playback init error: {ex}");
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
                    _ => Bass.CreateStream(musicUrl, 0, 0, BassFlags.Default | BassFlags.AsyncFile)
                };
                if (_currentStream == 0) return;
                Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _syncEndCallback);
                Bass.ChannelSetSync(_currentStream, SyncFlags.Stalled, 0, _syncFailCallback);
                ToggleEqualizer();
                if (!OutputMode.Contains("Wasapi") && OutputMode != "ASIO")
                    Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetSource error: {ex.Message}");
            }
        }

        public void PlayMusic(string musicUrl, bool isSettingChanged = false)
        {
            lock (_streamLock)
            {
                MusicUrl = musicUrl;
                if (IsFadingEnabled && IsPlaying && OutputMode == "DirectSound" && _currentStream != 0)
                    MusicFadeOut(MusicUrl, isSettingChanged);
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
            double currentPos = GetCurrentPosition();
            double totalPos = GetTotalPosition();
            double remainingTime = totalPos - currentPos;
            if (remainingTime < 3 || totalPos <= 0)
            {
                Stop();
                SetSource(newMusicUrl);
                Play(isSettingChanged);
                return;
            }
            int fadeOutDuration = (int)Math.Min(remainingTime * 500, 500);
            FadeOut(fadeOutDuration);
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
            if (_currentStream != 0) Bass.ChannelStop(_currentStream);
        }

        public async void PlayButton()
        {
            if (IsPlaying)
            {
                switch (OutputMode)
                {
                    case var mode when mode.Contains("Wasapi"): BassWasapi.Stop(); break;
                    case "ASIO": BassAsio.Stop(); break;
                    default:
                        if (IsFadingEnabled) { FadeOut(); await Task.Delay(550); Bass.ChannelStop(_currentStream); }
                        else Bass.ChannelStop(_currentStream);
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
                        case var mode when mode.Contains("Wasapi"): BassWasapi.Start(); break;
                        case "ASIO": BassAsio.Start(); break;
                        default:
                            if (IsFadingEnabled) FadeIn(volume);
                            Bass.ChannelPlay(_currentStream, false);
                            break;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(MusicUrl))
                    PlayMusic(MusicUrl);
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
                var mode when mode.Contains("Wasapi") => InitializePlayback() && TryStart(() => { BassWasapi.Start(); }),
                "ASIO" => InitializePlayback() && TryStart(() => { BassAsio.Start(); }),
                _ => TryStart(() => { Bass.ChannelPlay(_currentStream, false); if (IsFadingEnabled) FadeIn(volume); })
            };
            if (!success)
            {
                Bass.ChannelPlay(_currentStream, false);
                if (IsFadingEnabled) FadeIn(volume);
            }
            if (IsEqualizerEnabled) SetEqualizer();
            IsPlaying = true;
            _mmpIpcService.PlayStateUpdate(IsPlaying);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryStart(Action action)
        {
            try { action(); return true; } catch { return false; }
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

        public void UpdateSettings(IpcSetting s)
        {
            OutputMode = s.OutputMode ?? "DirectSound";
            BassOutputDeviceId = s.BassOutputDeviceId;
            BassASIODeviceId = s.BassASIODeviceId;
            Latency = s.Latency;
            IsDopEnabled = s.IsDopEnabled;
            dsdGain = s.DsdGain;
            dsdPcmFreq = s.DsdPcmFreq;
            IsEqualizerEnabled = s.IsEqualizerEnabled;
            volume = s.Volume;
            IsFadingEnabled = s.IsFadeEnabled;
            if (s.IsSettingChanged)
                ChangingSetting();
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
                    BassWasapi.SetVolume(WasapiVolumeTypes.WindowsHybridCurve, (float)volume);
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
            return Bass.ChannelBytes2Seconds(_currentStream, positionBytes) > 0
                ? Bass.ChannelBytes2Seconds(_currentStream, positionBytes) : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetTotalPosition()
        {
            if (_currentStream == 0) return 0;
            var totalBytes = Bass.ChannelGetLength(_currentStream);
            return Bass.ChannelBytes2Seconds(_currentStream, totalBytes) > 0
                ? Bass.ChannelBytes2Seconds(_currentStream, totalBytes) : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double AdjustPlaybackPosition(int seconds)
        {
            if (!IsPlaying || _currentStream == 0) return 0;
            double newPosition = GetCurrentPosition() + seconds;
            newPosition = Math.Clamp(newPosition, 0, GetTotalPosition());
            ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
            return newPosition > 0 ? newPosition : 0;
        }

        public void ChangingSetting()
        {
            try
            {
                lock (_streamLock)
                {
                    var currentTime = GetCurrentPosition();
                    if (IsPlaying)
                    {
                        Stop();
                        SetSource(MusicUrl!);
                        Play(true);
                    }
                    else
                    {
                        SetSource(MusicUrl!);
                    }
                    ChangeWaveChannelTime(TimeSpan.FromSeconds(currentTime));
                }
            }
            catch { }
        }

        private void DisposeStream()
        {
            if (_currentStream != 0) { Bass.StreamFree(_currentStream); _currentStream = 0; }
            StopWasapiPlayback();
            StopAsioPlayback();
            _peakEQ?.Dispose();
            _peakEQ = null;
        }

        private void StopWasapiPlayback()
        {
            try
            {
                if (BassWasapi.IsStarted) BassWasapi.Stop(true);
                BassWasapi.Free();
                IsPlaying = false;
            }
            catch (Exception ex) { Debug.WriteLine($"Stop WASAPI error: {ex}"); }
        }

        private void StopAsioPlayback()
        {
            try
            {
                if (BassAsio.IsStarted) BassAsio.Stop();
                BassAsio.Free();
                IsPlaying = false;
            }
            catch (Exception ex) { Debug.WriteLine($"Stop ASIO error: {ex}"); }
        }

        public (int id, string name)[] GetWasapiDevices()
        {
            var list = new List<(int, string)>();
            int count = BassWasapi.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                if (BassWasapi.GetDeviceInfo(i, out var info) && info.IsEnabled && info.Type != WasapiDeviceType.Microphone)
                    list.Add((i, info.Name ?? string.Empty));
            }
            return list.ToArray();
        }

        public (int id, string name)[] GetAsioDevices()
        {
            var list = new List<(int, string)>();
            int count = BassAsio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                if (BassAsio.GetDeviceInfo(i, out var info))
                    list.Add((i, info.Name ?? string.Empty));
            }
            return list.ToArray();
        }

        public void Dispose()
        {
            _peakEQ?.Dispose();
            DisposeStream();
            _fadeTimer?.Dispose();
            BassManager.Free();
        }
    }
}

using BassPlayerSharp.Manager;
using BassPlayerSharp.Model;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Dsd;
using ManagedBass.Fx;
using ManagedBass.Wasapi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace BassPlayerSharp.Service
{
    public class PlayBackService
    {
        private readonly TcpService _tcpService;
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
        private readonly float[] _eqFrequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }; // 10频段
        private double MinDb = -60;
        private double MaxDb = 0;
        private double MiddleDb = -30;
        private PeakEQ _peakEQ;
        public bool IsPlaying = false;
        public string PlayMode = "ListLoop";
        public string OutputMode = "DirectSound";
        public int BassOutputDeviceId = -1;
        public int BassASIODeviceId = 0;
        public int Latency = 400;
        public bool IsDopEnabled = false;
        public string MusicUrl;
        public int dsdGain = 6;
        public int dsdPcmFreq = 88200;
        public bool IsEqualizerEnabled = false;
        public static Dictionary<string, double> equalizer = new()
        {
            {"32Hz", 0},   // 32Hz 初始增益 0dB
            {"64Hz", 0},   // 64Hz 初始增益 0dB
            {"125Hz", 0},  // 125Hz 初始增益 0dB
            {"250Hz", 0},  // 250Hz 初始增益 0dB
            {"500Hz", 0},  // 500Hz 初始增益 0dB
            {"1kHz", 0},   // 1kHz 初始增益 0dB
            {"2kHz", 0},   // 2kHz 初始增益 0dB
            {"4kHz", 0},   // 4kHz 初始增益 0dB
            {"8kHz", 0},   // 8kHz 初始增益 0dB
            {"16kHz", 0}   // 16kHz 初始增益 0dB
        };

        public static readonly Dictionary<float, string> FloatToString = new Dictionary<float, string>
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

        public PlayBackService(TcpService tcpService)
        {
            _tcpService = tcpService;
            BassManager.Initialize();
            _syncEndCallback = OnPlayBackEnded;
            _syncFailCallback = OnPlaybackFailed;
            _myWasapiProcedure = OnWasapiProc;
            _myAsioProcedure = OnAsioProc;
        }

        private void OnPlaybackFailed(int Handle, int Channel, int Data, nint User)
        {
            IsPlaying = false;
        }

        private void OnPlayBackEnded(int Handle, int Channel, int Data, nint User)
        {
            IsPlaying = false;
            _tcpService.PlayBackEnded(IsPlaying);
        }

        private int OnWasapiProc(IntPtr buffer, int length, IntPtr user)
        {
            if (_currentStream != 0)
            {
                return Bass.ChannelGetData(_currentStream, buffer, length);
            }
            return 0;
        }

        private int OnAsioProc(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (_currentStream != 0)
            {
                return Bass.ChannelGetData(user.ToInt32(), buffer, length);
            }
            return 0;
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

        public void ToggleEqualizer()
        {            
            if (IsEqualizerEnabled
               && !(IsDopEnabled
               && (OutputMode.Contains("WasapiExclusive") || OutputMode == "ASIO")
               && (Path.GetExtension(MusicUrl).Equals(".dsf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(MusicUrl).Equals(".dff", StringComparison.OrdinalIgnoreCase)))
              )
            {
                try
                {
                    if (_currentStream != 0)
                    {
                        _peakEQ = new PeakEQ(_currentStream, Q: 0, Bandwith: 1.0);
                        // 为每个频段添加Band
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
        }

        public void SetEqualizerGain(int bandIndex, float gain)
        {
            if (bandIndex < 0 || bandIndex >= _eqFrequencies.Length)
            {
                return;
            }
            if (_peakEQ == null)
            {
                return;
            }
            try
            {
                // 使用UpdateBand方法更新指定频段的增益
                _peakEQ.UpdateBand(_bandIndices[bandIndex], gain);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置均衡器参数失败: {ex.Message}");
            }
        }

        public void SetEqualizer()
        {
            if (_peakEQ is null) return;
            for (int i = 0; i < 10; i++)
            {
                _peakEQ.UpdateBand(_bandIndices[i], (float)equalizer[FloatToString[_eqFrequencies[i]]]);
            }
        }

        public void ClearEqualizer()
        {
            DisposeEq();
        }

        private bool SwitchDevice(ChannelInfo channelInfo)
        {
            bool result = false;
            switch (OutputMode)
            {
                case "WasapiShared":
                    result = BassWasapi.Init(BassOutputDeviceId,
                            channelInfo.Frequency,
                            channelInfo.Channels,
                            WasapiInitFlags.Shared,
                            Latency / 1000.0f, 0, _myWasapiProcedure);
                    break;
                case "WasapiExclusivePush":
                    result = BassWasapi.Init(BassOutputDeviceId,
                            channelInfo.Frequency,
                            channelInfo.Channels,
                            WasapiInitFlags.Exclusive,
                            Latency / 1000.0f, Latency / 8000.0f, _myWasapiProcedure);
                    break;
                case "WasapiExclusiveEvent":
                    result = BassWasapi.Init(BassOutputDeviceId,
                            channelInfo.Frequency,
                            channelInfo.Channels,
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
                // 初始化播放模式
                Bass.ChannelGetInfo(_currentStream, out var channelInfo);
                var result = SwitchDevice(channelInfo);
                if (!result)
                {
                    StopWasapiPlayback();
                    StopAsioPlayback();
                    result = SwitchDevice(channelInfo);
                    if (!result)
                    {
                        return false;
                    }
                }
                // 设置音量
                if (OutputMode.Contains("WasapiShared"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                }
                else if (OutputMode.Contains("WasapiExclusive"))
                {
                    if (volume > 0.7)
                    {
                        volume = (float)DbToLinear(MiddleDb);
                        BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)MiddleDb);
                        // 更改ui音量
                    }
                    else
                    {
                        BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
                    }
                }
                else if (OutputMode == "ASIO")
                {

                    if (IsDopEnabled
                        && (Path.GetExtension(MusicUrl).Equals(".dsf", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(MusicUrl).Equals(".dff", StringComparison.OrdinalIgnoreCase))
                        )
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
                        BassAsio.Rate = channelInfo.Frequency;
                    }
                    BassAsio.ChannelSetVolume(false, -1, volume);
                }
                Debug.WriteLine($"WASAPI模式启动成功");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex, $"启动WASAPI独占模式时出错");
                return false;
            }
        }

        private void SetSource(string MusicUrl)
        {
            try
            {
                DisposeStream();
                BassDsd.DefaultGain = dsdGain;
                BassDsd.DefaultFrequency = dsdPcmFreq;
                if (OutputMode.Contains("WasapiExclusive"))
                {
                    if (IsDopEnabled && (Path.GetExtension(MusicUrl).Equals(".dsf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(MusicUrl).Equals(".dff", StringComparison.OrdinalIgnoreCase)))
                    {
                        _currentStream = BassDsd.CreateStream(MusicUrl, 0, 0, BassFlags.DSDOverPCM | BassFlags.Float | BassFlags.Decode | BassFlags.AsyncFile);
                    }
                    else
                    {
                        _currentStream = Bass.CreateStream(MusicUrl, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                    }
                }
                else if (OutputMode.Contains("WasapiShared"))
                {
                    _currentStream = Bass.CreateStream(MusicUrl, 0, 0, BassFlags.Unicode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                }
                else if (OutputMode == "ASIO")
                {
                    if (IsDopEnabled && (Path.GetExtension(MusicUrl).Equals("dsf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(MusicUrl).Equals("dff", StringComparison.OrdinalIgnoreCase)))
                    {
                        _currentStream = BassDsd.CreateStream(MusicUrl, 0, 0, BassFlags.DSDRaw | BassFlags.Decode | BassFlags.AsyncFile);
                    }
                    else
                    {
                        _currentStream = Bass.CreateStream(MusicUrl, 0, 0, BassFlags.Float | BassFlags.AsyncFile | BassFlags.Decode);
                    }
                }
                else
                {
                    _currentStream = Bass.CreateStream(MusicUrl, 0, 0, BassFlags.Default | BassFlags.AsyncFile);
                }
                if (_currentStream == 0)
                {
                    return;
                }
                Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _syncEndCallback); // 设置播放结束回调
                Bass.ChannelSetSync(_currentStream, SyncFlags.Stalled, 0, _syncFailCallback); // 设置播放失败回调
                ToggleEqualizer();
                // 根据模式设置音量
                if (!OutputMode.Contains("Wasapi") && OutputMode != "ASIO")
                {
                    Bass.ChannelSetAttribute(
                        _currentStream,
                        ChannelAttribute.Volume,
                        volume
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetSource异常: {ex.Message}");
            }
        }

        public void PlayMusic(string musicUrl,bool isSettingChanged = false)
        {
            lock (_streamLock)
            {
                MusicUrl = musicUrl;
                Stop();
                SetSource(musicUrl);
                Play(isSettingChanged);
            }
        }

        public void Stop()
        {
            if (_currentStream != 0)
            {
                Bass.ChannelStop(_currentStream);
                //停止回调
            }
        }

        public void PlayButton()
        {
            if (IsPlaying)
            {
                if (OutputMode.Contains("Wasapi"))
                {
                    BassWasapi.Stop();
                }
                else if (OutputMode == "ASIO")
                {
                    BassAsio.Stop();
                }
                else
                {
                    Bass.ChannelStop(_currentStream);
                }
                isPausing = true;
                IsPlaying = false;
                //回调
            }
            else
            {
                if (_currentStream != 0)
                {
                    if (OutputMode.Contains("Wasapi"))
                    {
                        BassWasapi.Start();
                    }
                    else if (OutputMode == "ASIO")
                    {
                        BassAsio.Start();
                    }
                    else
                    {
                        Bass.ChannelPlay(_currentStream, false);
                    }
                }
                else
                {
                    //播放当前回调
                    if (!string.IsNullOrWhiteSpace(MusicUrl)) {
                        PlayMusic(MusicUrl);
                    }
                }
                isPausing = false;
                IsPlaying = true;
                //播放状态回调
                //App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                //{
                //    MusicBrowseViewModel.IsPlaying = true;
                //});
                //MusicBrowseViewModel.StartProgressTimer();
            }
            _tcpService.PlayStateUpdate(IsPlaying);
        }

        public void Play(bool isSettingChanged = false)
        {
            if (_currentStream != 0)
            {
                if (OutputMode.Contains("Wasapi"))
                {
                    // 独占模式下使用WASAPI播放
                    if (InitializePlayback())
                    {
                        BassWasapi.Start();
                    }
                    else
                    {
                        // 如果独占模式启动失败，回退到共享模式
                        Bass.ChannelPlay(_currentStream, false);
                    }
                }
                else if (OutputMode == "ASIO")
                {
                    if (InitializePlayback())
                    {
                        BassAsio.Start();
                    }
                    else
                    {
                        // 如果ASIO模式启动失败，回退到共享模式
                        Bass.ChannelPlay(_currentStream, false);
                    }
                }
                else
                {
                    // 共享模式下直接播放
                    Bass.ChannelPlay(_currentStream, false);
                }
                if (IsEqualizerEnabled)
                {
                    SetEqualizer();
                }
                IsPlaying = true;
                _tcpService.PlayStateUpdate(IsPlaying);
                //播放回调
                //MusicBrowseViewModel.StartProgressTimer();
                //App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                //{
                //    try
                //    {
                //        MusicBrowseViewModel.ProgressSliderMax = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));
                //        if (isSettingChanged)
                //        {
                //            MusicBrowseViewModel.ProgressSlider = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream));
                //        }
                //        else
                //        {
                //            MusicBrowseViewModel.ProgressSlider = 0;
                //        }
                //        AppSettings.isPlaying = true;
                //        MusicBrowseViewModel.IsPlaying = true;
                //        MusicBrowseViewModel.UpdatePlayPauseButtonIcon();
                //        _ = MusicDatabaseService.SavePlayState([.. MusicBrowseViewModel.SequentialPlayingList], AppData.PlayMode, MusicBrowseViewModel.CurrentPlayingMusic?.Id, volume, AppData.sortOrder);
                //    }
                //    catch (Exception)
                //    {
                //    }
                //});
            }
        }

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

        public void UpdateSettings(string settings) {
            var ipcSetting = System.Text.Json.JsonSerializer.Deserialize(settings,IpcSettingJsonContext.Default.IpcSetting);
            if (ipcSetting is not null)
            {
                PlayMode = ipcSetting.PlayMode;
                OutputMode = ipcSetting.OutputMode;
                BassOutputDeviceId = ipcSetting.BassOutputDeviceId;
                BassASIODeviceId = ipcSetting.BassASIODeviceId;
                Latency = ipcSetting.Latency;
                IsDopEnabled = ipcSetting.IsDopEnabled;
                dsdGain = ipcSetting.dsdGain;
                dsdPcmFreq = ipcSetting.dsdPcmFreq;
                IsEqualizerEnabled = ipcSetting.IsEqualizerEnabled;
                this.volume =ipcSetting.Volume;
                if (ipcSetting.IsSettingChanged)
                {
                    ChangingSetting();
                }
            }           
        }

        public void SetVolume(double volume)
        {
            this.volume = (float)volume;
            if (_currentStream != 0)
            {
                if (OutputMode.Contains("WasapiExclusive"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.LogaritmicCurve, (float)LinearToDb(volume));
                }
                else if (OutputMode.Contains("WasapiShared"))
                {
                    BassWasapi.SetVolume(WasapiVolumeTypes.Session, (float)volume);
                }
                else if (OutputMode == "ASIO")
                {
                    BassAsio.ChannelSetVolume(false, -1, volume);
                }
                else
                {
                    Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, volume);
                }
            }
        }

        public double GetCurrentPosition()
        {
            if (_currentStream != 0)
            {
                var positionBytes = Bass.ChannelGetPosition(_currentStream);
                return Bass.ChannelBytes2Seconds(_currentStream, positionBytes);
            }
            return 0;
        }

        public double GetTotalPosition()
        {
            if (_currentStream != 0)
            {
                var totalBytes = Bass.ChannelGetLength(_currentStream);
                return Bass.ChannelBytes2Seconds(_currentStream, totalBytes);
            }
            return 0;
        }

        public double AdjustPlaybackPosition(int seconds)
        {
            double newPosition = 0;
            if (IsPlaying)
            {
                if (_currentStream != 0)
                {
                    newPosition = GetCurrentPosition() + seconds;
                    newPosition = Math.Max(0, Math.Min(newPosition, GetTotalPosition()));
                    ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
                }
            }
            return newPosition;
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
            catch
            {
            }

        }

        public double LinearToDb(double linearValue)
        {
            if (linearValue <= 0)
                return MinDb;
            if (linearValue >= 1)
            {
                return MaxDb;
            }
            // 映射到0到-65.25dB的范围
            double dbValue = MaxDb + (MinDb - MaxDb) * (1 - Math.Log10(9 * linearValue + 1) / Math.Log10(10));
            return dbValue;
        }

        public double DbToLinear(double dbValue)
        {
            dbValue = Math.Clamp(dbValue, MinDb, MaxDb);
            if (dbValue <= MinDb)
                return 0;
            double dbPosition = (dbValue - MaxDb) / (MinDb - MaxDb);
            return (Math.Pow(10, (1 - dbPosition) * Math.Log10(10)) - 1) / 9;
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
                Debug.WriteLine(ex, $"停止WASAPI播放时出错");
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
                var asioFree = BassAsio.Free();
                if (!asioFree)
                {
                    Debug.WriteLine($"释放ASIO失败: {Bass.LastError}");
                }
                else
                {
                    Debug.WriteLine($"释放ASIO成功");
                }
                IsPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex, $"停止ASIO播放时出错");
            }
        }
        public void DisposeEq()
        {
            _peakEQ?.Dispose();
            _peakEQ = null;
        }

        public void Dispose()
        {
            DisposeEq();
            DisposeStream();
            BassManager.Free();
        }
    }
}

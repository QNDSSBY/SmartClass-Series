using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage;

namespace SmartClassNight
{
    /// <summary>
    /// 点歌系统的播放内核（**真实音波**）。
    ///
    /// 与旧的 MediaPlayer 方案不同，这里用 `Windows.Media.Audio.AudioGraph` 播放：
    /// 音频文件 → 输出设备（听得见）+ FrameOutputNode（拿得到**真实的 PCM 采样**）。
    /// 在 `QuantumStarted` 回调里取一帧浮点采样 → 加汉宁窗 → 做 1024 点 FFT →
    /// 把频谱按对数分成 30 个频段 → 得到 30 个真实能量值，界面直接用它画音波柱。
    ///
    /// 对外接口与原来的 <see cref="AudioEngine"/> 保持一致（Load/Play/Pause/Stop/Position/Duration/Volume + 事件），
    /// 这样界面几乎不用改；只是多了 <see cref="CopyLevels"/>（真实频谱）与 <see cref="HasRealSpectrum"/>。
    /// 若本机 AudioGraph 不可用（无声卡/创建失败），自动退回 MediaPlayer 播放（此时音波为平线，不再假装跳动）。
    /// </summary>
    public sealed class MusicEngine : IDisposable
    {
        // ==================== 频谱参数 ====================
        public const int BandCount = 30;          // 音波柱数量
        private const int FftSize = 1024;         // FFT 点数（2 的幂）
        private const double MinFreq = 40;        // 最低显示频率
        private const double MaxFreq = 16000;     // 最高显示频率

        private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private readonly float[] _window = new float[FftSize];
        private readonly float[] _samples = new float[FftSize * 8];   // 原始交错采样（最多 8 声道）
        private readonly float[] _ring = new float[FftSize];          // 1024 点单声道环形缓冲
        private int _ringPos;                                         // 下一个写入位置
        private int _ringFilled;                                      // 已写入的采样数
        private double _agc = 0.12;                                   // 自适应增益（让不同响度的音乐都能打满）
        private readonly double[] _levels = new double[BandCount];    // 供界面读取的真实频谱（0…1）
        private readonly object _levelLock = new();

        private AudioGraph? _graph;
        private AudioDeviceOutputNode? _deviceOutput;
        private AudioFrameOutputNode? _frameOutput;
        private AudioFileInputNode? _fileInput;
        private int _channels = 2;
        private bool _graphReady;
        private bool _disposed;
        private DateTime _lastLevelLog = DateTime.MinValue;   // 频谱诊断日志节流
        private int _quantumCount;                            // 已处理量子数（诊断用）
        private bool _playing;
        private double _volume = 0.8;

        // AudioGraph 不可用时的兜底播放器（音波会保持平线）
        private AudioEngine? _fallback;

        public event Action? MediaOpened;
        public event Action? Ended;
        public event Action<string>? Failed;

        public MusicEngine(double volume = 0.8)
        {
            _volume = Math.Clamp(volume, 0, 1);
            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // 汉宁窗
        }

        /// <summary>是否拿到了真实频谱（AudioGraph 可用）。</summary>
        public bool HasRealSpectrum => _graphReady;

        public bool HasMedia { get; private set; }

        public bool IsPlaying => _playing;

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 1);
                try { if (_deviceOutput != null) _deviceOutput.OutgoingGain = _volume; } catch { }
                try { if (_fallback != null) _fallback.Volume = _volume; } catch { }
            }
        }

        public TimeSpan Duration
        {
            get
            {
                try { if (_fileInput != null) return _fileInput.Duration; } catch { }
                try { if (_fallback != null) return _fallback.Duration; } catch { }
                return TimeSpan.Zero;
            }
        }

        public TimeSpan Position
        {
            get
            {
                try { if (_fileInput != null) return _fileInput.Position; } catch { }
                try { if (_fallback != null) return _fallback.Position; } catch { }
                return TimeSpan.Zero;
            }
            set
            {
                try { if (_fileInput != null) _fileInput.Seek(value); } catch { }
                try { if (_fallback != null) _fallback.Position = value; } catch { }
            }
        }

        // ==================== 初始化 ====================

        private async Task EnsureGraphAsync()
        {
            if (_graphReady || _graph != null) return;
            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.Media)
                {
                    QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
                };
                var graphResult = await AudioGraph.CreateAsync(settings);
                if (graphResult.Status != AudioGraphCreationStatus.Success) throw new Exception("AudioGraph 创建失败：" + graphResult.Status);

                _graph = graphResult.Graph;

                var outResult = await _graph.CreateDeviceOutputNodeAsync();
                if (outResult.Status != AudioDeviceNodeCreationStatus.Success) throw new Exception("输出设备创建失败：" + outResult.Status);
                _deviceOutput = outResult.DeviceOutputNode;
                _deviceOutput.OutgoingGain = _volume;

                _frameOutput = _graph.CreateFrameOutputNode();
                _graph.QuantumStarted += OnQuantumStarted;
                _graphReady = true;

                MainWindow.IslandLog($"点歌：AudioGraph 就绪（真实音波可用，采样率 {_graph.EncodingProperties.SampleRate}）");
            }
            catch (Exception ex)
            {
                _graphReady = false;
                _graph = null;
                _deviceOutput = null;
                _frameOutput = null;
                _fallback ??= new AudioEngine(_volume);
                MainWindow.IslandLog($"点歌：AudioGraph 不可用，退回 MediaPlayer（音波为平线）：{ex.Message}");
            }
        }

        // ==================== 播放控制 ====================

        public async void Load(string filePath, bool autoPlay = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
                HasMedia = true;
                ClearLevels();

                await EnsureGraphAsync();

                if (!_graphReady || _graph == null)
                {
                    // 兜底：MediaPlayer 播放
                    _fallback ??= new AudioEngine(_volume);
                    _fallback.MediaOpened += () => Raise(() => MediaOpened?.Invoke());
                    _fallback.Ended += () => Raise(() => Ended?.Invoke());
                    _fallback.Failed += m => Raise(() => Failed?.Invoke(m));
                    _fallback.Load(filePath, autoPlay);
                    _playing = autoPlay;
                    Raise(() => MediaOpened?.Invoke());
                    return;
                }

                // 断开并释放上一条
                try { _fileInput?.Dispose(); } catch { }
                _fileInput = null;

                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var fileResult = await _graph.CreateFileInputNodeAsync(file);
                if (fileResult.Status != AudioFileNodeCreationStatus.Success)
                {
                    _playing = false;
                    Raise(() => Failed?.Invoke("无法打开音频文件：" + fileResult.Status));
                    return;
                }

                _fileInput = fileResult.FileInputNode;
                _fileInput.AddOutgoingConnection(_deviceOutput!);
                if (_frameOutput != null) _fileInput.AddOutgoingConnection(_frameOutput);
                _fileInput.FileCompleted += OnFileCompleted;

                _channels = Math.Max(1, (int)_graph.EncodingProperties.ChannelCount);
                Raise(() => MediaOpened?.Invoke());

                if (autoPlay) Play();
                else _playing = false;
            }
            catch (Exception ex)
            {
                _playing = false;
                Raise(() => Failed?.Invoke(ex.Message));
            }
        }

        public void Play()
        {
            try
            {
                if (_graphReady && _graph != null)
                {
                    _graph.Start();
                    _playing = true;
                    return;
                }
                _fallback?.Play();
                _playing = true;
            }
            catch { }
        }

        public void Pause()
        {
            try
            {
                if (_graphReady && _graph != null)
                {
                    _graph.Stop();
                    _playing = false;
                    ClearLevels();
                    return;
                }
                _fallback?.Pause();
                _playing = false;
            }
            catch { }
        }

        public void Stop()
        {
            Pause();
            Position = TimeSpan.Zero;
        }

        public void TogglePause()
        {
            if (_playing) Pause();
            else Play();
        }

        private void OnFileCompleted(AudioFileInputNode sender, object args)
        {
            try
            {
                _playing = false;
                ClearLevels();
                try { _graph?.Stop(); } catch { }
                Raise(() => Ended?.Invoke());
            }
            catch { }
        }

        private void Raise(Action action)
        {
            try
            {
                if (_queue != null && !_queue.HasThreadAccess) _queue.TryEnqueue(() => { try { action(); } catch { } });
                else action();
            }
            catch { }
        }

        // ==================== 真实频谱 ====================

        /// <summary>把当前 30 个频段的真实能量值拷给界面（0…1）。</summary>
        public void CopyLevels(double[] destination)
        {
            try
            {
                lock (_levelLock)
                    for (int i = 0; i < BandCount && i < destination.Length; i++)
                        destination[i] = _levels[i];
            }
            catch { }
        }

        private void ClearLevels()
        {
            try
            {
                lock (_levelLock)
                    for (int i = 0; i < BandCount; i++)
                        _levels[i] = 0;
            }
            catch { }
        }

        /// <summary>AudioGraph 每次量子（约 10ms）回调一次：取真实 PCM → FFT → 频段能量。</summary>
        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            try
            {
                if (!_playing || _frameOutput == null) return;

                using var frame = _frameOutput.GetFrame();
                if (frame == null) return;

                using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
                using var reference = buffer.CreateReference();

                // 拿到底层 PCM 指针（float32 交错）
                // 注意：CsWinRT 下不能把 WinRT 对象强转成 [ComImport] 接口（会 InvalidCastException），
                // 这里用 QueryInterface + 直接调用 vtable 第 4 个方法（GetBuffer）的方式取指针。
                if (!TryGetBufferPointer(reference, out IntPtr dataPtr, out uint capacity)) return;
                if (dataPtr == IntPtr.Zero || capacity < 4) return;

                int total = (int)(capacity / sizeof(float));
                int need = Math.Min(total, _samples.Length);
                Marshal.Copy(dataPtr, _samples, 0, need);

                // 多声道混合成单声道，写进 1024 点环形缓冲
                // （一个量子只有几百个采样，攒满 1024 点再做 FFT，频率分辨率才够）
                int frames = need / _channels;
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < _channels; c++) sum += _samples[i * _channels + c];
                    _ring[_ringPos] = sum / _channels;
                    _ringPos++;
                    if (_ringPos >= FftSize) _ringPos = 0;
                    if (_ringFilled < FftSize) _ringFilled++;
                }

                // 还没攒够半窗（刚开始播放）先不算
                if (_ringFilled < FftSize / 2) return;

                // 按时间顺序取出最近的一窗（未满时后面补零）
                int have = _ringFilled;
                int start = _ringFilled >= FftSize ? _ringPos : 0;   // 环形缓冲中最老的采样位置
                for (int i = 0; i < FftSize; i++)
                {
                    float s = i < have ? _ring[(start + i) % FftSize] : 0f;
                    _re[i] = s * _window[i];
                    _im[i] = 0;
                }

                Fft(_re, _im);

                // 频段能量（对数分频）：取每个频段内各 bin 的幅度均值
                int sampleRate = (int)(_graph?.EncodingProperties.SampleRate ?? 48000);
                double binHz = (double)sampleRate / FftSize;
                double[] bandValues = new double[BandCount];
                double logMin = Math.Log10(MinFreq), logMax = Math.Log10(MaxFreq);
                for (int b = 0; b < BandCount; b++)
                {
                    double f0 = Math.Pow(10, logMin + (logMax - logMin) * b / BandCount);
                    double f1 = Math.Pow(10, logMin + (logMax - logMin) * (b + 1) / BandCount);
                    int k0 = Math.Max(1, (int)(f0 / binHz));
                    int k1 = Math.Min(FftSize / 2 - 1, Math.Max(k0, (int)(f1 / binHz)));
                    double sum = 0; int count = 0;
                    for (int k = k0; k <= k1; k++)
                    {
                        double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / (FftSize / 4.0);
                        sum += mag * mag;                       // 能量
                        count++;
                    }
                    double rms = count > 0 ? Math.Sqrt(sum / count) : 0;
                    // 转成视觉上好看的 0…1：对数压缩 + 轻微提升高频（人耳/灯光习惯）
                    double v = Math.Log10(1 + rms * 200) / 1.7;
                    bandValues[b] = Math.Clamp(v * (1.0 + 0.25 * b / BandCount), 0, 1);
                }

                // 自适应增益：跟随最近的最强频段，让不同响度的曲目都能打满柱子；安静时自然回落
                double peak = 0;
                for (int b = 0; b < BandCount; b++) if (bandValues[b] > peak) peak = bandValues[b];
                if (peak > 0.02)   // 极安静时不跟随，避免把底噪放大
                    _agc = peak > _agc ? _agc + (peak - _agc) * 0.30 : _agc * 0.985 + peak * 0.015;
                double norm = Math.Min(6.0, 1.0 / Math.Clamp(_agc * 1.10, 0.10, 1.0));

                // 平滑：上升快、回落慢（与真实电平一致的手感）
                lock (_levelLock)
                {
                    for (int b = 0; b < BandCount; b++)
                    {
                        double target = Math.Clamp(bandValues[b] * norm, 0, 1);
                        double cur = _levels[b];
                        _levels[b] = target > cur ? cur + (target - cur) * 0.65 : cur * 0.80 + target * 0.20;
                    }
                }

                // 诊断：每 2 秒记一行（真实频谱是否取到、最强频段落在哪里）
                _quantumCount++;
                if (_quantumCount < 6000 && (DateTime.Now - _lastLevelLog).TotalSeconds >= 10)
                {
                    _lastLevelLog = DateTime.Now;
                    int bestBand = 0; double best = 0;
                    for (int b = 0; b < BandCount; b++) if (bandValues[b] > best) { best = bandValues[b]; bestBand = b; }
                    MainWindow.IslandLog($"音波诊断：量子#{_quantumCount} 声道{_channels} 缓冲{_ringFilled} 最强频段#{bestBand}={best:F2} 增益{_agc:F2}");
                }
            }
            catch (Exception ex)
            {
                // 回调里绝不能抛异常（会直接结束进程）；这里只记一次，便于排查“音波一直平线”
                if ((DateTime.Now - _lastLevelLog).TotalSeconds >= 2)
                {
                    _lastLevelLog = DateTime.Now;
                    MainWindow.IslandLog($"音波诊断异常：{ex.GetType().Name} {ex.Message}");
                }
            }
        }

        /// <summary>迭代式 radix-2 FFT（原地）。</summary>
        private static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cwr = 1, cwi = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float ur = re[a], ui = im[a];
                        float vr = re[b] * cwr - im[b] * cwi;
                        float vi = re[b] * cwi + im[b] * cwr;
                        re[a] = ur + vr; im[a] = ui + vi;
                        re[b] = ur - vr; im[b] = ui - vi;
                        float nwr = cwr * wr - cwi * wi;
                        cwi = cwr * wi + cwi * wr;
                        cwr = nwr;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_graph != null) _graph.QuantumStarted -= OnQuantumStarted;
                try { _fileInput?.Dispose(); } catch { }
                try { _frameOutput?.Dispose(); } catch { }
                try { _deviceOutput?.Dispose(); } catch { }
                try { _graph?.Dispose(); } catch { }
                _fallback?.Dispose();
            }
            catch { }
            ClearLevels();
        }

        // ==================== WinRT 互操作：取 AudioFrame 的 PCM 指针 ====================

        /// <summary>IMemoryBufferByteAccess::GetBuffer 的 vtable 委托（第 4 个方法）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GetBufferDelegate(IntPtr thisPtr, out IntPtr buffer, out uint capacity);

        private static readonly Guid IMemoryBufferByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

        /// <summary>
        /// 通过 QueryInterface + 直接调用 vtable 拿到 IMemoryBufferReference 的字节指针。
        /// （CsWinRT 下无法用 (IMemoryBufferByteAccess)reference 强转，会抛 InvalidCastException。）
        /// </summary>
        private static bool TryGetBufferPointer(Windows.Foundation.IMemoryBufferReference reference,
            out IntPtr data, out uint capacity)
        {
            data = IntPtr.Zero;
            capacity = 0;
            IntPtr qi = IntPtr.Zero;
            try
            {
                if (reference is not WinRT.IWinRTObject winrtObject) return false;
                IntPtr thisPtr = winrtObject.NativeObject.ThisPtr;
                if (thisPtr == IntPtr.Zero) return false;

                Guid iid = IMemoryBufferByteAccessIid;
                int hr = Marshal.QueryInterface(thisPtr, ref iid, out qi);
                if (hr != 0 || qi == IntPtr.Zero) return false;

                IntPtr vtbl = Marshal.ReadIntPtr(qi);
                IntPtr fn = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);   // QI/AddRef/Release 之后第 4 个
                var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(fn);
                getBuffer(qi, out data, out capacity);
                return data != IntPtr.Zero;
            }
            catch { return false; }
            finally
            {
                if (qi != IntPtr.Zero) { try { Marshal.Release(qi); } catch { } }
            }
        }
    }
}

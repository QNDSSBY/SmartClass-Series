using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SmartClassNight
{
    /// <summary>
    /// 新消息提示音。声音与播放逻辑均对齐网页 https://f18.llt-service.cn/weather 的通知逻辑：
    ///
    /// 网页实现（playNotifySound）：
    ///   let audioAlert = new Audio('/alarm.mp3');
    ///   audioAlert.onerror = () =&gt; { …WebAudio 振荡器 880Hz / gain 0.3 / 0.5s 指数衰减… };
    ///   audioAlert.play().catch(() =&gt; audioAlert.onerror());
    ///
    /// 本地实现与网页一一对应：
    ///   1) 优先播放随程序安装的 Assets\alarm.mp3（与网页 /alarm.mp3 是同一个文件，离线也能响）；
    ///   2) 本地文件缺失时，联网从 https://f18.llt-service.cn/alarm.mp3 下载到
    ///      %LOCALAPPDATA%\金华一中科技校园套件\alarm.mp3 再播放（等价于网页直接取服务器音频）；
    ///   3) 两者都不可用/播放失败时，用同参数的 880Hz、0.5 秒指数衰减“叮”声兜底
    ///      （网页的 WebAudio 兜底音），保证“收到新消息一定有提示音”。
    ///
    /// 播放放在独立后台线程的单一队列里，多条消息同一批到达只响一次，不会叠加成一串噪音。
    /// 开关由“设置 → 个性化 → 新消息提示音”控制，保存在 classroom.json 的 notifySound 字段。
    /// </summary>
    public static class NotificationSound
    {
        /// <summary>网页 new Audio('/alarm.mp3') 对应的服务器地址（站点根目录）。</summary>
        private const string RemoteUrl = "https://f18.llt-service.cn/alarm.mp3";

        /// <summary>音量（对齐网页 Audio 默认音量 1.0）。</summary>
        private const double Volume = 1.0;

        /// <summary>兜底音：与网页 WebAudio 兜底一致的 880Hz / 0.5 秒 / 指数衰减。</summary>
        private const int ToneHz = 880;
        private const int ToneMs = 500;
        private const double ToneGain = 0.3;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly BlockingCollection<byte> Requests = new();
        private static readonly object InitLock = new();

        private static Thread? _worker;
        private static MediaPlayer? _player;
        private static bool _mediaOpened;
        private static bool _mediaFailed;

        /// <summary>服务器音频的本地缓存（网页依赖网络，这里做一次缓存后续离线可用）。</summary>
        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "alarm.mp3");

        /// <summary>是否播放提示音（读取本地配置 classroom.json 的 notifySound，默认开）。</summary>
        public static bool Enabled => ClassDataService.NotifySoundEnabled;

        /// <summary>收到新消息时调用；内部异步播放，立即返回，不阻塞轮询线程。</summary>
        public static void Play() => Play(false);

        /// <summary>试听（设置页按钮用）：忽略提示音开关，强制播放一次。</summary>
        public static void Preview() => Play(true);

        public static void Play(bool force)
        {
            try
            {
                if (!force && !Enabled) return;
                EnsureWorker();
                Requests.Add(0);   // 队列满/已完成等异常由内部吞掉，提示音失败绝不影响主流程
            }
            catch { }
        }

        // ==================== 后台播放线程 ====================

        private static void EnsureWorker()
        {
            if (_worker != null) return;
            lock (InitLock)
            {
                if (_worker != null) return;
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "NotificationSound"
                };
                _worker.Start();
            }
        }

        private static void WorkerLoop()
        {
            while (!Requests.IsCompleted)
            {
                try { Requests.Take(); }
                catch { return; }

                // 同一批（同一次轮询的多条消息）只响一次，避免连续叠加播放
                while (Requests.TryTake(out _)) { }

                PlayOnce();
            }
        }

        private static void PlayOnce()
        {
            try
            {
                string? file = EnsureLocalFile();
                if (file != null && TryPlayFile(file)) return;
            }
            catch (Exception ex)
            {
                MainWindow.IslandLog($"提示音播放异常：{ex.Message}");
            }
            PlayToneFallback();
        }

        // ==================== 1) 内置文件 → 2) 服务器兜底 ====================

        private static string? EnsureLocalFile()
        {
            foreach (string path in CandidatePaths())
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
                }
                catch { }
            }
            return TryDownloadFromServer();
        }

        private static string[] CandidatePaths() => new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "alarm.mp3"),
            Path.Combine(AppContext.BaseDirectory, "alarm.mp3"),
            CachePath
        };

        /// <summary>本地文件缺失时，按网页的做法直接从服务器取 /alarm.mp3 并留作缓存。</summary>
        private static string? TryDownloadFromServer()
        {
            try
            {
                byte[] data = Http.GetByteArrayAsync(RemoteUrl).GetAwaiter().GetResult();
                if (data.Length < 1024) return null;
                string? dir = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(CachePath, data);
                MainWindow.IslandLog($"提示音本地文件缺失，已从服务器下载 {data.Length} 字节 → {CachePath}");
                return CachePath;
            }
            catch (Exception ex)
            {
                MainWindow.IslandLog($"提示音服务器下载失败：{ex.Message}");
                return null;
            }
        }

        // ==================== MediaPlayer 播放 ====================

        private static bool TryPlayFile(string path)
        {
            try
            {
                if (_player == null)
                {
                    _player = new MediaPlayer
                    {
                        Volume = Volume,
                        AudioCategory = MediaPlayerAudioCategory.Alerts,
                        AutoPlay = false
                    };
                    try { _player.CommandManager.IsEnabled = false; } catch { }   // 不进入系统媒体控制中心
                    _player.MediaOpened += (_, _) => _mediaOpened = true;
                    _player.MediaFailed += (_, e) =>
                    {
                        _mediaFailed = true;
                        MainWindow.IslandLog($"提示音媒体打开失败：{e.ErrorMessage}");
                    };
                }

                _mediaOpened = false;
                _mediaFailed = false;

                // 每次都用新的 MediaSource，确保重新打开、从头播放
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                _player.PlaybackSession.Position = TimeSpan.Zero;
                _player.Play();

                // 最多等 3 秒：确已开始播放才算成功，否则退化到 880Hz 兜底音（对齐网页 audio.onerror）
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    if (_mediaFailed) return false;

                    var state = _player.PlaybackSession.PlaybackState;
                    if (_mediaOpened ||
                        state == MediaPlaybackState.Playing ||
                        state == MediaPlaybackState.Paused)
                    {
                        MainWindow.IslandLog($"提示音已播放：{path}");
                        return true;
                    }
                }

                MainWindow.IslandLog("提示音未能开始播放（超时），改用 880Hz 兜底音");
                return false;
            }
            catch (Exception ex)
            {
                MainWindow.IslandLog($"提示音播放失败：{ex.Message}");
                return false;
            }
        }

        // ==================== 3) 880Hz 兜底音（网页 WebAudio 兜底的等价实现） ====================

        // SND_MEMORY 要求内存在播放期间（异步）一直有效，这里整进程固定住这一小段 WAV
        private static byte[]? _toneWav;
        private static GCHandle _toneHandle;
        private static readonly object ToneLock = new();

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_MEMORY = 0x0004;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

        private static void PlayToneFallback()
        {
            try
            {
                lock (ToneLock)
                {
                    if (_toneWav == null)
                    {
                        _toneWav = BuildToneWav(ToneHz, ToneMs, ToneGain);
                        _toneHandle = GCHandle.Alloc(_toneWav, GCHandleType.Pinned);
                    }
                    if (PlaySound(_toneHandle.AddrOfPinnedObject(), IntPtr.Zero,
                                  SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                    {
                        MainWindow.IslandLog($"提示音已播放：880Hz 兜底音（{ToneMs}ms）");
                        return;
                    }
                }
            }
            catch { }

            try
            {
                Console.Beep(ToneHz, ToneMs);
                MainWindow.IslandLog("提示音已播放：Console.Beep(880, 500) 兜底");
            }
            catch (Exception ex)
            {
                MainWindow.IslandLog($"提示音全部失败：{ex.Message}");
            }
        }

        /// <summary>生成 16bit 单声道 WAV：880Hz 正弦 + 指数衰减包络（听感对齐网页的 gain 指数斜坡）。</summary>
        private static byte[] BuildToneWav(int hz, int ms, double gain)
        {
            const int rate = 44100;
            int count = Math.Max(1, rate * ms / 1000);
            var pcm = new byte[count * 2];
            double seconds = ms / 1000.0;
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / rate;
                double env = Math.Exp(-t / (seconds / 2.0));   // 0.5 秒内衰减到约 -8.7dB（≈网页 0.00001 斜坡的听感）
                double v = Math.Sin(2 * Math.PI * hz * t) * gain * env;
                short s = (short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + pcm.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                    // fmt 块长度
            w.Write((short)1);              // PCM
            w.Write((short)1);              // 单声道
            w.Write(rate);                  // 采样率
            w.Write(rate * 2);              // 字节率
            w.Write((short)2);              // 块对齐
            w.Write((short)16);             // 位深
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);
            w.Flush();
            return stream.ToArray();
        }
    }
}

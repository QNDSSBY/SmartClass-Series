using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SmartClassNight
{
    /// <summary>
    /// 音频播放引擎（基于 Windows.Media.Playback.MediaPlayer，支持 mp3 / wav / m4a / wma / flac 等系统解码格式）。
    /// 提供播放/暂停/跳转/音量/时长/结束事件，供**打铃**（BellService）与**点歌**（MusicPage）共用。
    /// 每个实例是独立的播放通道：打铃与点歌互不影响，音量也各自独立。
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        private readonly MediaPlayer _player = new();
        private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();

        /// <summary>媒体已打开（此时 Duration 有效）。</summary>
        public event Action? MediaOpened;
        /// <summary>播放到结尾（自然结束）。</summary>
        public event Action? Ended;
        /// <summary>打开/解码失败（参数为错误信息）。</summary>
        public event Action<string>? Failed;

        public AudioEngine(double volume = 1.0)
        {
            try
            {
                _player.AudioCategory = MediaPlayerAudioCategory.Media;
                _player.Volume = Math.Clamp(volume, 0, 1);
                _player.CommandManager.IsEnabled = false;   // 不进系统媒体控制中心
            }
            catch { }

            _player.MediaOpened += (_, _) => Raise(() => MediaOpened?.Invoke());
            _player.MediaEnded += (_, _) => Raise(() => Ended?.Invoke());
            _player.MediaFailed += (_, e) => Raise(() => Failed?.Invoke(e.ErrorMessage ?? "播放失败"));
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

        /// <summary>当前是否有已加载的媒体。</summary>
        public bool HasMedia { get; private set; }

        /// <summary>音量 0…1。</summary>
        public double Volume
        {
            get { try { return _player.Volume; } catch { return 1; } }
            set { try { _player.Volume = Math.Clamp(value, 0, 1); } catch { } }
        }

        /// <summary>媒体总时长（未打开时为零）。</summary>
        public TimeSpan Duration
        {
            get { try { return _player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
        }

        /// <summary>当前播放位置（可读可写，写=跳转）。</summary>
        public TimeSpan Position
        {
            get { try { return _player.PlaybackSession?.Position ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
            set
            {
                try
                {
                    if (_player.PlaybackSession != null) _player.PlaybackSession.Position = value;
                }
                catch { }
            }
        }

        public bool IsPlaying
        {
            get
            {
                try { return _player.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing; }
                catch { return false; }
            }
        }

        /// <summary>打开文件；autoPlay=true 时打开后立即播放。</summary>
        public void Load(string filePath, bool autoPlay = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
                HasMedia = true;
                _player.Source = MediaSource.CreateFromUri(new Uri(filePath));
                if (autoPlay) _player.Play();
            }
            catch (Exception ex)
            {
                Raise(() => Failed?.Invoke(ex.Message));
            }
        }

        public void Play()
        {
            try { _player.Play(); } catch { }
        }

        public void Pause()
        {
            try { _player.Pause(); } catch { }
        }

        public void Stop()
        {
            try
            {
                _player.Pause();
                if (_player.PlaybackSession != null) _player.PlaybackSession.Position = TimeSpan.Zero;
            }
            catch { }
        }

        public void TogglePause()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        public void Dispose()
        {
            try
            {
                _player.Pause();
                _player.Source = null;
                _player.Dispose();
            }
            catch { }
        }
    }

    /// <summary>音频文件标签（标题 / 艺术家 / 专辑 / 内嵌封面）。</summary>
    public sealed class AudioTagInfo
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        /// <summary>内嵌封面已导出到的本地图片路径（无封面为空串）。</summary>
        public string CoverPath { get; set; } = "";
    }

    /// <summary>
    /// 读取 mp3 的 ID3v2 标签（TIT2/TPE1/TALB）与内嵌封面（APIC）。
    /// 封面直接按原始字节写成 jpg/png 缓存文件，交给 XAML 的 BitmapImage 加载（无需解码库）。
    /// </summary>
    public static class AudioTags
    {
        private const int MaxTagBytes = 12 * 1024 * 1024;   // 标签体积上限（防异常文件）

        public static string CoverCacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "covers");

        private static int Synchsafe(byte[] b, int offset)
            => ((b[offset] & 0x7F) << 21) | ((b[offset + 1] & 0x7F) << 14) | ((b[offset + 2] & 0x7F) << 7) | (b[offset + 3] & 0x7F);

        private static int BigEndian(byte[] b, int offset)
            => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

        /// <summary>解析 ID3v2.3 / v2.4 标签（v2.2 的 3 字符帧不支持）。</summary>
        public static AudioTagInfo ReadId3(string filePath)
        {
            var info = new AudioTagInfo();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var header = new byte[10];
                if (fs.Read(header, 0, 10) != 10) return info;
                if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return info;

                int major = header[3];
                if (major < 3) return info;                 // 2.x 帧结构不同，直接跳过
                bool v24 = major >= 4;
                int size = Synchsafe(header, 6);
                if (size <= 0 || size > MaxTagBytes) return info;

                var tag = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int n = fs.Read(tag, read, size - read);
                    if (n <= 0) break;
                    read += n;
                }

                int pos = 0;
                while (pos + 10 <= read)
                {
                    string id = Encoding.ASCII.GetString(tag, pos, 4);
                    if (string.IsNullOrWhiteSpace(id) || id[0] == '\0') break;

                    int frameSize = v24 ? Synchsafe(tag, pos + 4) : BigEndian(tag, pos + 4);
                    if (frameSize <= 0 || pos + 10 + frameSize > read) break;
                    int dataStart = pos + 10;

                    if (id == "TIT2") info.Title = DecodeTextFrame(tag, dataStart, frameSize);
                    else if (id == "TPE1") info.Artist = DecodeTextFrame(tag, dataStart, frameSize);
                    else if (id == "TALB") info.Album = DecodeTextFrame(tag, dataStart, frameSize);
                    else if (id == "APIC" && string.IsNullOrEmpty(info.CoverPath))
                        info.CoverPath = ExtractCover(tag, dataStart, frameSize, filePath);

                    pos = dataStart + frameSize;
                }
            }
            catch { }
            return info;
        }

        /// <summary>按 ID3 文本帧的编码字节解码（0=Latin1 1=UTF-16 2=UTF-16BE 3=UTF-8）。</summary>
        private static string DecodeTextFrame(byte[] data, int start, int length)
        {
            try
            {
                if (length <= 1) return "";
                byte encoding = data[start];
                int offset = start + 1;
                int count = length - 1;
                string text = DecodeString(data, offset, count, encoding);
                return text.Trim('\0', ' ', '\uFEFF');
            }
            catch { return ""; }
        }

        private static string DecodeString(byte[] data, int offset, int count, byte encoding)
        {
            if (count <= 0) return "";
            switch (encoding)
            {
                case 1:   // UTF-16（带 BOM）
                    if (count >= 2 && data[offset] == 0xFF && data[offset + 1] == 0xFE)
                        return Encoding.Unicode.GetString(data, offset + 2, count - 2);
                    if (count >= 2 && data[offset] == 0xFE && data[offset + 1] == 0xFF)
                        return Encoding.BigEndianUnicode.GetString(data, offset + 2, count - 2);
                    return Encoding.Unicode.GetString(data, offset, count);
                case 2:
                    return Encoding.BigEndianUnicode.GetString(data, offset, count);
                case 3:
                    return Encoding.UTF8.GetString(data, offset, count);
                default:
                    return Encoding.Latin1.GetString(data, offset, count);
            }
        }

        /// <summary>提取 APIC 帧里的封面图片，写入缓存目录并返回路径。</summary>
        private static string ExtractCover(byte[] data, int start, int length, string sourceFile)
        {
            try
            {
                if (length < 6) return "";
                byte encoding = data[start];
                int pos = start + 1;

                // MIME（Latin1，0 结尾）
                int mimeStart = pos;
                while (pos < start + length && data[pos] != 0) pos++;
                string mime = Encoding.Latin1.GetString(data, mimeStart, pos - mimeStart).ToLowerInvariant();
                pos++;                       // 跳过 0

                pos++;                       // 图片类型

                // 描述（按编码不同，终止符宽度不同）
                if (encoding == 1 || encoding == 2)
                {
                    while (pos + 1 < start + length && !(data[pos] == 0 && data[pos + 1] == 0)) pos += 2;
                    pos += 2;
                }
                else
                {
                    while (pos < start + length && data[pos] != 0) pos++;
                    pos++;
                }

                int imageLength = start + length - pos;
                if (imageLength <= 0 || pos >= data.Length) return "";

                string ext = mime.Contains("png") ? ".png" : mime.Contains("gif") ? ".gif" : mime.Contains("bmp") ? ".bmp" : ".jpg";
                if (imageLength > 8 && data[pos] == 0x89 && data[pos + 1] == 0x50) ext = ".png";

                Directory.CreateDirectory(CoverCacheDir);
                string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    Encoding.UTF8.GetBytes(sourceFile + "|" + imageLength))).Substring(0, 16);
                string outPath = Path.Combine(CoverCacheDir, hash + ext);
                if (!File.Exists(outPath))
                    File.WriteAllBytes(outPath, data.AsSpan(pos, imageLength).ToArray());
                return outPath;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 读取标签：先解析 ID3（含封面），标题为空时回退系统媒体属性（标题/艺术家/专辑）。
        /// 建议在后台线程调用（Task.Run）。
        /// </summary>
        public static async Task<AudioTagInfo> ReadAsync(string filePath)
        {
            var info = ReadId3(filePath);
            if (!string.IsNullOrWhiteSpace(info.Title) && !string.IsNullOrWhiteSpace(info.Artist)) return info;

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var props = await file.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Title", "System.Music.Artist", "System.Music.AlbumTitle"
                });

                string Get(string key)
                {
                    if (props.TryGetValue(key, out var value) && value is string s) return s.Trim();
                    if (props.TryGetValue(key, out var v2) && v2 is System.Collections.Generic.IEnumerable<string> list)
                        foreach (var artist in list) if (!string.IsNullOrWhiteSpace(artist)) return artist.Trim();
                    return "";
                }

                if (string.IsNullOrWhiteSpace(info.Title)) info.Title = Get("System.Title");
                if (string.IsNullOrWhiteSpace(info.Artist)) info.Artist = Get("System.Music.Artist");
                if (string.IsNullOrWhiteSpace(info.Album)) info.Album = Get("System.Music.AlbumTitle");
            }
            catch { }

            return info;
        }
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SmartClassNight
{
    /// <summary>
    /// 点歌页面（QQ 音乐风格）：黑胶唱片 + 歌曲信息 + 装饰性音波 + 播放列表 + 底部控制条。
    /// 播放内核复用项目里已有的 <see cref="AudioEngine"/>（与打铃互不干扰），标签/封面复用 <see cref="AudioTags"/>。
    /// 本页被横向 Pager 常驻托管，不自行设置页面宽高，随卡片自适应填充。
    /// </summary>
    public sealed partial class MusicPage : UserControl
    {
        // ==================== 常量 ====================
        private const int BarCount = 30;                 // 音波竖条数量
        private const double BarFlatHeight = 8;          // 暂停/停止时的“平线”高度
        private const double BarMaxHeight = 210;         // 竖条最高高度（画布 220，留一点余量）
        private const double VinylStepDegrees = 1.2;     // 唱片每次旋转角度
        private const double SeekTouchTolerance = 1.5;   // 判断“用户拖动进度条”的容差（秒）

        /// <summary>支持的音频扩展名（也用于筛选，避免把非音频文件塞进列表）。</summary>
        private static readonly string[] SupportedExtensions =
            { ".mp3", ".wav", ".m4a", ".flac", ".wma", ".aac", ".ogg" };

        // ==================== 状态 ====================
        private readonly MusicEngine _engine = new(0.8);           // 播放内核（AudioGraph：带真实音波频谱）
        private readonly List<TrackItem> _tracks = new();         // 播放列表
        private readonly double[] _barLevel = new double[BarCount];        // 当前柱高（像素，做平滑用）
        private readonly double[] _realLevels = new double[MusicEngine.BandCount];  // 引擎给的真实频段能量（0…1）

        private DispatcherTimer? _progressTimer;   // 200ms：刷新进度/时间
        private DispatcherTimer? _vinylTimer;      // 40ms：唱片旋转
        private DispatcherTimer? _waveTimer;       // 33ms：音波跳动
        private DispatcherQueue? _queue;           // 音频事件可能来自后台线程，统一切回 UI 线程

        private int _index = -1;                   // 当前曲目在列表中的下标（-1 = 无）
        private bool _seeking;                     // 用户正在拖动进度条
        private bool _suppressSeek;                // 程序化写入进度条时，忽略 ValueChanged 回调
        private bool _disposed;                    // 已释放
        private bool _suppressSelection;           // 程序化刷新列表时屏蔽 SelectionChanged（防栈溢出递归）
        private bool _inPlayIndex;                 // PlayIndex 重入保护

        public MusicPage()
        {
            this.InitializeComponent();

            _queue = DispatcherQueue.GetForCurrentThread();

            // 音波竖条：等宽列 + 底部对齐的圆角 Border
            BuildSpectrum();

            // 播放内核事件（AudioEngine 已保证在 UI 线程回调，这里再做一次兜底派发）
            _engine.MediaOpened += OnEngineMediaOpened;
            _engine.Ended += OnEngineEnded;
            _engine.Failed += OnEngineFailed;

            // 音量默认 80%
            _engine.Volume = VolumeSlider.Value / 100.0;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _progressTimer.Tick += ProgressTimer_Tick;

            _vinylTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _vinylTimer.Tick += VinylTimer_Tick;

            _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _waveTimer.Tick += WaveTimer_Tick;

            this.Loaded += MusicPage_Loaded;
            this.Unloaded += MusicPage_Unloaded;

            UpdateTrackInfo(null);
            UpdatePlaylistHint();
            _progressTimer.Start();
            _vinylTimer.Start();
            _waveTimer.Start();
        }

        // ==================== 生命周期 ====================

        private void MusicPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 页面被 Pager 常驻托管，Loaded 可能多次触发：这里只保证定时器在跑
            _progressTimer?.Start();
            _vinylTimer?.Start();
            _waveTimer?.Start();
        }

        private void MusicPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 窗口关闭/页面卸载时统一释放，避免 MediaPlayer 泄漏
            if (_disposed) return;
            _disposed = true;

            try { _progressTimer?.Stop(); } catch { }
            try { _vinylTimer?.Stop(); } catch { }
            try { _waveTimer?.Stop(); } catch { }

            try
            {
                _engine.MediaOpened -= OnEngineMediaOpened;
                _engine.Ended -= OnEngineEnded;
                _engine.Failed -= OnEngineFailed;
                _engine.Dispose();
            }
            catch { }
        }

        // ==================== 音波（装饰性，非 FFT） ====================

        /// <summary>生成 30 根等宽竖条（Border，圆角 2，半透明蓝）。</summary>
        private void BuildSpectrum()
        {
            SpectrumCanvas.Children.Clear();
            SpectrumCanvas.ColumnDefinitions.Clear();

            for (int i = 0; i < BarCount; i++)
            {
                SpectrumCanvas.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bar = new Border
                {
                    Width = 8,
                    Height = BarFlatHeight,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xAA, 0x2B, 0x7D, 0xE9))
                };
                Grid.SetColumn(bar, i);
                SpectrumCanvas.Children.Add(bar);
            }

            for (int i = 0; i < BarCount; i++)
                _barLevel[i] = BarFlatHeight;
        }

        /// <summary>
        /// 音波：**真实频谱**。数据来自 <see cref="MusicEngine"/> 在 AudioGraph 回调里算出的
        /// 30 个频段能量（真实 PCM → 汉宁窗 → 1024 点 FFT → 对数分频），这里只做高度映射与平滑。
        /// AudioGraph 不可用时（无真实频谱）柱子保持平线，不再用随机数假装跳动。
        /// </summary>
        private void WaveTimer_Tick(object? sender, object e)
        {
            if (_disposed) return;

            bool playing = _engine.IsPlaying;
            _engine.CopyLevels(_realLevels);                        // 0…1 的真实频段能量

            for (int i = 0; i < SpectrumCanvas.Children.Count && i < BarCount; i++)
            {
                if (SpectrumCanvas.Children[i] is not Border bar) continue;

                double target = BarFlatHeight;
                if (playing && _engine.HasRealSpectrum)
                {
                    double v = i < _realLevels.Length ? _realLevels[i] : 0;
                    // 低柱也留一点高度，避免安静时整排贴地
                    target = BarFlatHeight + Math.Clamp(v, 0, 1) * (BarMaxHeight - BarFlatHeight);
                }

                // 上升稍快、回落稍慢（与真实电平表手感一致）
                double factor = target > _barLevel[i] ? 0.55 : 0.22;
                _barLevel[i] += (target - _barLevel[i]) * factor;
                bar.Height = Math.Max(4, _barLevel[i]);
            }
        }

        // ==================== 唱片旋转 ====================

        private void VinylTimer_Tick(object? sender, object e)
        {
            if (_disposed) return;
            if (!_engine.IsPlaying) return;                    // 暂停时停在当前角度

            double angle = VinylRotate.Angle + VinylStepDegrees;
            if (angle >= 360) angle -= 360;
            VinylRotate.Angle = angle;
        }

        // ==================== 进度 / 时间 ====================

        private void ProgressTimer_Tick(object? sender, object e)
        {
            if (_disposed) return;

            try
            {
                var duration = _engine.Duration;
                var position = _engine.Position;

                // 灵动岛信息条：播放中左右拉长显示「歌名 | 进度时间」，停止后 4 秒恢复原状
                UpdateIslandInfo(playing: _engine.IsPlaying, position, duration);
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

                double totalSeconds = duration.TotalSeconds;
                if (totalSeconds > 1 && Math.Abs(SeekSlider.Maximum - totalSeconds) > 0.5)
                {
                    SeekSlider.Maximum = totalSeconds;          // 打开媒体后把进度条量程换成真实时长
                    SeekSlider.StepFrequency = Math.Max(0.1, totalSeconds / 200.0);
                }

                // 用户未拖动时才由定时器回写（避免与拖动打架）
                if (!_seeking)
                {
                    double value = Math.Min(position.TotalSeconds, SeekSlider.Maximum);
                    if (value < 0) value = 0;
                    SetSeekValue(value);                         // 程序化写入，不触发跳转逻辑
                }

                CurrentTimeText.Text = FormatTime(position);
                TotalTimeText.Text = FormatTime(duration);
            }
            catch { }

            UpdatePlayPauseVisual();
        }

        private static string FormatTime(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value.TotalHours >= 1) return value.ToString(@"h\:mm\:ss");
            return value.ToString(@"mm\:ss");
        }

        // ==================== 进度条拖动 ====================

        private void SeekSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _seeking = true;                                   // 开始拖动，暂停定时器回写
        }

        private void SeekSlider_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SeekToSliderValue();
            _seeking = false;
        }

        private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSeek) return;                         // 定时器/代码写入 → 不当作跳转

            // 只在“拖动中”或“数值偏离实际播放位置较多（键盘/滚轮操作）”时真正跳转
            if (_seeking)
            {
                SeekToSliderValue();
                return;
            }

            double actual = _engine.Position.TotalSeconds;
            if (_engine.HasMedia && Math.Abs(e.NewValue - actual) > SeekTouchTolerance)
                SeekToSliderValue();
        }

        /// <summary>程序化写入进度条数值（被 _suppressSeek 屏蔽，不会回灌成跳转）。</summary>
        private void SetSeekValue(double value)
        {
            _suppressSeek = true;
            try { SeekSlider.Value = value; }
            catch { }
            finally { _suppressSeek = false; }
        }

        /// <summary>把进度条当前值写回播放位置（失败不抛异常，程序化写入时自动跳过）。</summary>
        private void SeekToSliderValue()
        {
            try
            {
                if (!_engine.HasMedia || _suppressSeek) return;
                _suppressSeek = true;
                try { _engine.Position = TimeSpan.FromSeconds(Math.Max(0, SeekSlider.Value)); }
                finally { _suppressSeek = false; }
            }
            catch { }
        }

        // ==================== 播放内核事件 ====================

        private void OnEngineMediaOpened()
        {
            RunOnUi(() =>
            {
                try
                {
                    var duration = _engine.Duration;
                    if (duration.TotalSeconds > 1)
                    {
                        SeekSlider.Maximum = duration.TotalSeconds;
                        SeekSlider.StepFrequency = Math.Max(0.1, duration.TotalSeconds / 200.0);
                    }
                    TotalTimeText.Text = FormatTime(duration);
                    UpdatePlayPauseVisual();
                }
                catch { }
            });
        }

        private void OnEngineEnded()
        {
            // 自然结束 → 自动下一曲（到末尾则停在最后一首，不再循环）
            RunOnUi(() => PlayRelative(1));
        }

        private void OnEngineFailed(string message)
        {
            RunOnUi(() =>
            {
                ShowError(string.IsNullOrWhiteSpace(message) ? "无法播放该文件" : "无法播放该文件：" + message);
                UpdatePlayPauseVisual();
            });
        }

        /// <summary>音频事件可能来自后台线程，统一切回 UI 线程。</summary>
        private void RunOnUi(Action action)
        {
            try
            {
                if (_disposed) return;
                if (_queue != null && !_queue.HasThreadAccess) _queue.TryEnqueue(() => { try { action(); } catch { } });
                else action();
            }
            catch { }
        }

        // ==================== 正在播放的曲目信息 ====================

        /// <summary>刷新左卡的标题 / 艺术家 · 专辑 / 文件名 / 封面；传 null 表示占位状态。</summary>
        private void UpdateTrackInfo(TrackItem? track)
        {
            try
            {
                HideError();

                if (track == null)
                {
                    TrackTitleText.Text = "未选择歌曲";
                    TrackArtistText.Text = "请从下方列表选择或“选择文件…”添加音频";
                    TrackFileText.Text = "";
                    CoverImage.Source = null;
                    CoverFallbackIcon.Visibility = Visibility.Visible;
                    VinylRotate.Angle = 0;
                    SetSeekValue(0);
                    SeekSlider.Maximum = 1;
                    CurrentTimeText.Text = "00:00";
                    TotalTimeText.Text = "00:00";
                    return;
                }

                TrackTitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? track.Name : track.Title!;

                string artist = string.IsNullOrWhiteSpace(track.Artist) ? "未知艺术家" : track.Artist!;
                string album = string.IsNullOrWhiteSpace(track.Album) ? "" : track.Album!;
                TrackArtistText.Text = string.IsNullOrEmpty(album) ? artist : artist + " · " + album;

                TrackFileText.Text = string.IsNullOrWhiteSpace(track.Name) ? track.Path : track.Name;

                ApplyCover(track.CoverPath);

                VinylRotate.Angle = 0;                          // 换曲重置唱片角度
                SetSeekValue(0);
                CurrentTimeText.Text = "00:00";

                var duration = _engine.Duration;
                SeekSlider.Maximum = duration.TotalSeconds > 1 ? duration.TotalSeconds : 1;
                TotalTimeText.Text = FormatTime(duration);
                UpdatePlayPauseVisual();
            }
            catch { }
        }

        /// <summary>设置封面图；封面为空或文件不存在时回退为音符图标。</summary>
        private void ApplyCover(string? coverPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
                {
                    CoverImage.Source = new BitmapImage(new Uri(coverPath!));
                    CoverFallbackIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CoverImage.Source = null;
                    CoverFallbackIcon.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                // 图片损坏/被占用时也不让界面出问题
                CoverImage.Source = null;
                CoverFallbackIcon.Visibility = Visibility.Visible;
            }
        }

        /// <summary>按当前播放状态刷新播放/暂停按钮外观。</summary>
        private void UpdatePlayPauseVisual()
        {
            try
            {
                bool playing = _engine.IsPlaying;
                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
                PlayPauseText.Text = playing ? "暂停" : "播放";
            }
            catch { }
        }

        private void ShowError(string message)
        {
            try
            {
                ErrorText.Text = message;
                ErrorText.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void HideError()
        {
            try
            {
                ErrorText.Text = "";
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // ==================== 灵动岛信息条（播放中显示歌名与进度时间） ====================

        private DateTime _islandInfoHiddenAt = DateTime.MinValue;   // 上次隐藏信息条的时间（宽限用）

        /// <summary>
        /// 播放中把「歌名 | 当前时间 / 总时长」送到灵动岛（左右拉长、高度不变）；
        /// 停止/暂停后延迟 4 秒恢复原状（避免短暂暂停导致闪烁）。
        /// </summary>
        private void UpdateIslandInfo(bool playing, TimeSpan position, TimeSpan duration)
        {
            try
            {
                var main = AppHost.Main;
                if (main == null) return;

                if (playing)
                {
                    var track = (_index >= 0 && _index < _tracks.Count) ? _tracks[_index] : null;
                    string title = track == null ? "" :
                        (!string.IsNullOrWhiteSpace(track.Title) ? track.Title : track.Name);
                    if (string.IsNullOrWhiteSpace(title)) title = "正在播放";

                    string timeText = duration > TimeSpan.Zero
                        ? $"{Format(position)} / {Format(duration)}"
                        : Format(position);

                    main.ShowIslandMusicInfo(title, timeText);
                    _islandInfoHiddenAt = DateTime.MinValue;
                }
                else if (_islandInfoHiddenAt == DateTime.MinValue)
                {
                    _islandInfoHiddenAt = DateTime.Now;      // 开始计时，4 秒后若仍未播放则恢复
                }
                else if ((DateTime.Now - _islandInfoHiddenAt).TotalSeconds >= 4)
                {
                    main.ClearIslandMusicInfo();
                    _islandInfoHiddenAt = DateTime.MaxValue;  // 已恢复，不重复调用
                }
            }
            catch { }
        }

        private static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
        // ==================== 播放列表 ====================

        /// <summary>刷新列表 UI（重新赋值 ItemsSource，保证“▶ 前缀/副标题”跟随状态更新）。</summary>
        private void RefreshPlaylist() => RefreshPlaylist(null);

        /// <summary>
        /// 刷新列表 UI；<paramref name="track"/> 非空时，若它正好是当前播放曲目，顺便刷新左侧歌曲信息。
        /// 重新赋值 ItemsSource 是为了让「▶ 前缀 / 艺术家·专辑」跟随播放状态更新。
        /// </summary>
        private void RefreshPlaylist(TrackItem? track)
        {
            // 程序化刷新列表期间，屏蔽 SelectionChanged —— 否则会形成
            // RefreshPlaylist → SelectedIndex 变化 → SelectionChanged → PlayIndex → RefreshPlaylist 的
            // 无限递归（表现为「添加歌曲时闪退」，实际是栈溢出 0xC00000FD）。
            _suppressSelection = true;
            try
            {
                string? currentPath = (_index >= 0 && _index < _tracks.Count) ? _tracks[_index].Path : null;

                for (int i = 0; i < _tracks.Count; i++)
                {
                    var item = _tracks[i];
                    bool isCurrent = currentPath != null &&
                                     string.Equals(item.Path, currentPath, StringComparison.OrdinalIgnoreCase);
                    item.DisplayName = (isCurrent ? "▶ " : "") + item.Name;
                    item.Subtitle = BuildSubtitle(item);
                }

                PlaylistView.ItemsSource = null;
                PlaylistView.ItemsSource = _tracks;

                if (_index >= 0 && _index < _tracks.Count && PlaylistView.SelectedIndex != _index)
                    PlaylistView.SelectedIndex = _index;

                PlaylistHeaderText.Text = "播放列表（" + _tracks.Count + " 首）";
                UpdatePlaylistHint();

                if (track != null && _index >= 0 && _index < _tracks.Count && ReferenceEquals(_tracks[_index], track))
                    UpdateTrackInfo(track);
            }
            catch { }
            finally { _suppressSelection = false; }
        }

        private static string BuildSubtitle(TrackItem item)
        {
            string artist = string.IsNullOrWhiteSpace(item.Artist) ? "" : item.Artist!;
            string album = string.IsNullOrWhiteSpace(item.Album) ? "" : item.Album!;
            if (artist.Length == 0 && album.Length == 0) return "未知艺术家";
            if (album.Length == 0) return artist;
            if (artist.Length == 0) return album;
            return artist + " · " + album;
        }

        private void UpdatePlaylistHint()
        {
            try
            {
                PlaylistEmptyHint.Visibility = _tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        // ==================== 添加文件 ====================

        private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            await AddFilesViaDialogAsync();
        }

        /// <summary>弹「打开文件」对话框选歌（Win32 对话框，稳定且一定显示在软件窗口之上）。</summary>
        private async Task AddFilesViaDialogAsync()
        {
            try
            {
                IntPtr owner = IntPtr.Zero;
                try { if (AppHost.Main != null) owner = WinRT.Interop.WindowNative.GetWindowHandle(AppHost.Main); } catch { }

                var paths = FileDialogs.PickFiles(owner, "选择音频文件（可多选）", FileDialogs.AudioFilter, multiSelect: true);
                if (paths.Count == 0) return;
                await AddPathsAsync(paths);
            }
            catch (Exception ex)
            {
                ShowError("添加文件失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把一批音频文件加入播放列表（对话框选择与诊断开关共用同一路径）。
        /// 返回实际加入的数量。
        /// </summary>
        public async Task<int> AddPathsAsync(IEnumerable<string> paths)
        {
            int added = 0;
            try
            {
                MainWindow.IslandLog("点歌：开始添加文件");
                bool wasEmpty = _tracks.Count == 0;
                var addedItems = new List<TrackItem>();

                foreach (var path in paths)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (!File.Exists(path)) continue;
                        if (!IsSupported(path)) continue;
                        if (_tracks.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
                            continue;                                // 按路径去重

                        var item = new TrackItem
                        {
                            Path = path,
                            Name = Path.GetFileName(path),
                            Title = Path.GetFileNameWithoutExtension(path),
                            Artist = "",
                            Album = "",
                            CoverPath = ""
                        };
                        _tracks.Add(item);
                        addedItems.Add(item);
                        added++;
                    }
                    catch { }
                }

                if (addedItems.Count == 0)
                {
                    MainWindow.IslandLog("点歌：没有可加入的文件");
                    return 0;
                }

                MainWindow.IslandLog($"点歌：已加入 {addedItems.Count} 首，刷新列表");
                RefreshPlaylist();
                MainWindow.IslandLog("点歌：列表已刷新");

                // 列表中原本没有可播内容 → 自动开播本次添加的第一首
                if (wasEmpty && !_engine.HasMedia)
                {
                    MainWindow.IslandLog("点歌：自动播放第一首");
                    PlayIndex(0, force: true);
                    MainWindow.IslandLog("点歌：播放请求已发出");
                }

                // 标签/封面在后台逐个读取，读完再刷新界面（不阻塞 UI 线程）
                await ReadTagsForAllAsync(addedItems);
                MainWindow.IslandLog("点歌：标签读取完成");
            }
            catch (Exception ex)
            {
                MainWindow.IslandLog($"点歌：添加失败 {ex.GetType().Name} {ex.Message}");
                ShowError("添加文件失败：" + ex.Message);
            }
            return added;
        }

        private static bool IsSupported(string path)
        {
            try
            {
                string ext = Path.GetExtension(path);
                return SupportedExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>后台逐个读取标签/封面，全部读完后在 UI 线程刷新列表（不阻塞界面）。</summary>
        private async Task ReadTagsForAllAsync(List<TrackItem> items)
        {
            foreach (var item in items)
            {
                // 单个文件失败不影响其余文件（ReadTagsAsync 内部已吞异常，这里再兜一层）
                try { await ReadTagsAsync(item); } catch { }
            }

            RunOnUi(() =>
            {
                try { RefreshPlaylist(); }
                catch { }
            });
        }

        /// <summary>
        /// 读取 ID3/系统属性（标题、艺术家、专辑、封面）；任何异常都只记录不抛出，
        /// **绝不能让异常逃到线程池（会直接结束进程）**。
        /// </summary>
        private static async Task ReadTagsAsync(TrackItem item)
        {
            try
            {
                var info = await Task.Run(async () =>
                {
                    try { return await AudioTags.ReadAsync(item.Path); }
                    catch { return null; }
                });

                if (info == null) return;

                if (!string.IsNullOrWhiteSpace(info.Title)) item.Title = info.Title;
                if (!string.IsNullOrWhiteSpace(info.Artist)) item.Artist = info.Artist;
                if (!string.IsNullOrWhiteSpace(info.Album)) item.Album = info.Album;
                if (!string.IsNullOrWhiteSpace(info.CoverPath)) item.CoverPath = info.CoverPath;
            }
            catch { }
        }

        // ==================== 播放控制 ====================

        /// <summary>播放列表中指定下标的曲目；force=true 时即使同一首也重新加载。</summary>
        private void PlayIndex(int index, bool force = false)
        {
            if (_inPlayIndex) return;                  // 防重入（配合 _suppressSelection 彻底断开递归链）
            _inPlayIndex = true;
            try
            {
                if (index < 0 || index >= _tracks.Count) return;
                if (!force && index == _index && _engine.HasMedia)
                {
                    _engine.Play();
                    UpdatePlayPauseVisual();
                    return;
                }

                _index = index;
                var track = _tracks[index];

                UpdateTrackInfo(track);
                RefreshPlaylist();
                _engine.Load(track.Path, true);                      // 失败会走 Failed 事件

                // 标签可能还没读完（例如刚添加就点击）→ 补读一次
                if (string.IsNullOrWhiteSpace(track.CoverPath) || string.IsNullOrWhiteSpace(track.Artist))
                    _ = ReadTagsThenRefreshAsync(track);
            }
            catch
            {
                ShowError("无法播放该文件");
            }
            finally { _inPlayIndex = false; }
        }

        private async Task ReadTagsThenRefreshAsync(TrackItem track)
        {
            await ReadTagsAsync(track);
            RunOnUi(() => RefreshPlaylist(track));
        }

        /// <summary>相对切换曲目；到列表末尾后停止（不循环）。</summary>
        private void PlayRelative(int delta)
        {
            try
            {
                if (_tracks.Count == 0) return;

                int next = _index + delta;
                if (_index < 0) next = 0;

                if (next < 0 || next >= _tracks.Count)
                {
                    // 已是最后一首：停止播放并保持界面不变
                    _engine.Stop();
                    UpdatePlayPauseVisual();
                    return;
                }

                PlayIndex(next, force: true);
            }
            catch { }
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 播放超过 3 秒时，“上一曲”先回到本曲开头（符合常见播放器习惯）
                if (_engine.HasMedia && _engine.Position.TotalSeconds > 3)
                {
                    _engine.Position = TimeSpan.Zero;
                    SetSeekValue(0);
                    CurrentTimeText.Text = "00:00";
                    return;
                }
                PlayRelative(-1);
            }
            catch { }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            PlayRelative(1);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_engine.HasMedia)
                {
                    // 还没加载任何媒体 → 从列表头开始播
                    if (_tracks.Count > 0) PlayIndex(0, force: true);
                    return;
                }

                _engine.TogglePause();
                UpdatePlayPauseVisual();                             // Ended 事件负责后续状态
            }
            catch
            {
                ShowError("播放控制失败");
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _suppressSeek = true;                                 // 清空过程中不让进度条抖动触发跳转
                try
                {
                    _engine.Stop();
                    _engine.Load("", false);                          // AudioEngine 对空路径直接忽略，等于停止
                }
                finally { _suppressSeek = false; }

                _tracks.Clear();
                _index = -1;
                RefreshPlaylist();
                UpdateTrackInfo(null);
                UpdatePlayPauseVisual();
            }
            catch { }
        }

        private void PlaylistView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_suppressSelection || _inPlayIndex) return;   // 程序化刷新/递归调用时不响应
                int index = PlaylistView.SelectedIndex;
                if (index < 0 || index >= _tracks.Count) return;
                if (index == _index && _engine.HasMedia) return;   // 已是当前曲目，不重复加载

                PlayIndex(index, force: index != _index);
            }
            catch { }
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                _engine.Volume = Math.Clamp(e.NewValue / 100.0, 0, 1);   // 0…100 → 0…1
            }
            catch { }
        }
    }

    /// <summary>播放列表条目（自包含，不依赖项目其他类）。</summary>
    public sealed class TrackItem
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string CoverPath { get; set; } = "";

        /// <summary>列表显示名（当前播放曲目带「▶ 」前缀）。</summary>
        public string DisplayName { get; set; } = "";
        /// <summary>列表副标题（艺术家 · 专辑）。</summary>
        public string Subtitle { get; set; } = "";
    }
}

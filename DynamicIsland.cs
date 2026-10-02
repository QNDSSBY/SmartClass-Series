using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;

namespace SmartClassNight
{
    /// <summary>
    /// iOS 风格“灵动岛”（视觉/动效参考 WinIslands；1.1.8 起改为**独立置顶窗口**）：
    /// - 空闲（收起态）：窗口整体隐藏 —— 屏幕上不显示任何信息与图案，但内部尺寸仍保持原版胶囊宽度，
    ///   以便下次收到消息时从原版宽度展开；
    /// - 收到消息：弹簧展开为通栏横幅（纯黑不透明背景），先“收到新消息”2 秒，再从右向左滚动消息两遍，
    ///   随后自动收起并**隐藏窗口**；窗口是置顶工具窗（不激活、不进任务栏），
    ///   因此无论当前前台是哪个程序，收到消息都能看到灵动岛；
    /// - 点击灵动岛：展开为**正方形**，用正常字号显示历史消息（含图片可再次点击查看）；
    /// - 尺寸形变由 DispatcherTimer 逐帧驱动弹簧（WinUI 中 Width/Height 属 dependent animation，
    ///   Storyboard 不会生效）；展开与收起均为 1.5 秒，使用同一套弹簧曲线。
    /// </summary>
    public sealed partial class IslandWindow
    {
        private enum IslandMode { Collapsed, Banner, History, Info }

        // ── 尺寸 ──
        private const double IslandCollapsedWidth = 150;     // 收起态宽度
        private const double IslandCollapsedHeight = 40;     // 收起态高度
        private const double IslandExpandedSideMargin = 96;  // 通栏横幅：左右各留 48px
        private const double IslandExpandedMinWidth = 320;
        private const double IslandCornerRadius = 28;        // 参考实现默认圆角（高度不足时取高度一半 → 半圆端）
        private const double IslandHistoryMinSide = 300;     // 方形历史视图边长
        private const double IslandHistoryMaxSide = 640;

        // ── 弹簧参数（移植自参考实现 SpringEase；此处调得更慢、更收敛）──
        private const double SpringStiffness = 200;
        private const double SpringMass = 1;
        private const double SpringDampingWidth = 26;        // 宽度：过冲 <0.1%（几乎不回弹）
        private const double SpringDampingHeight = 22;       // 高度：过冲约 2%
        private const double SpringTimeScale = 1.15;         // 振荡在时长内刚好收敛，避免“提前停住”
        private const int SpringTickMs = 16;
        private const double IslandAnimationMs = 1500;       // 展开/收起均为 1.5 秒（更慢）
        private const double IslandGlassTargetOpacity = 0.75;
        private const int CascadeDelayBaseMs = 90;
        private const int CascadeDelayStepMs = 70;
        private const int CascadeFadeMs = 340;
        private const int CascadeMoveMs = 420;
        private const double CascadeOffsetY = 10;

        // ── 消息流程 ──
        private const double IslandHeadlineSeconds = 2.0;
        private const int IslandMarqueeLoops = 2;
        private const double IslandMarqueeDelaySec = 0.45;
        private const double LineVisibleSeconds = 8.0;   // 纵向滚动时每行在可视区停留的时间（越大越慢，保证读完）
        private const int IslandHistoryMax = 100;

        private DispatcherTimer? _islandPhaseTimer;
        private DispatcherTimer? _islandSizeTimer;
        private Storyboard? _islandMarqueeStoryboard;
        private readonly List<IslandMessage> _islandMessages = new();
        private readonly List<IslandHistoryEntry> _islandHistory = new();
        private int _islandIndex;
        private IslandMode _islandMode = IslandMode.Collapsed;
        private double _islandExpandedWidth = IslandExpandedMinWidth;
        private double _islandExpandedHeight = 135;
        private double _islandBannerHeight = 135;      // 横幅当前高度（长消息按需加高，字体保持不变）
        private double _islandHistorySide = 520;
        private double _screenWidth = 1920;
        private double _screenHeight = 1080;

        /// <summary>宿主主窗口：用于顶部元素淡出、图片预览等回调（灵动岛现在是独立窗口）。</summary>
        private MainWindow? _host;

        /// <summary>主页面（第一屏）是否正在显示 —— 显示时灵动岛以“纯黑胶囊”常驻。</summary>
        private bool _homeVisible;

        // 信息条（点歌播放中 / 打铃中）
        private bool _infoActive;
        private string _infoLeft = "";
        private string _infoRight = "";

        // 逐帧弹簧状态
        private double _springFromW, _springToW, _springFromH, _springToH;
        private double _springDurationMs = IslandAnimationMs;
        private double _springMaxW, _springMaxH;
        private DateTime _springStart;

        /// <summary>灵动岛是否处于展开态（横幅或方形历史），供桌面页同步顶部元素淡出。</summary>
        public bool IsIslandExpanded => _islandMode != IslandMode.Collapsed;

        // ==================== 诊断日志 ====================

        private static readonly object IslandLogLock = new();
        private static readonly string IslandLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "island.log");

        internal static void IslandLog(string message)
        {
            try
            {
                string? dir = Path.GetDirectoryName(IslandLogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                lock (IslandLogLock)
                    File.AppendAllText(IslandLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch { }
        }

        // ==================== 初始化 ====================

        /// <summary>由主窗口在构造时调用一次：绑定宿主、准备内容（不显示窗口）。</summary>
        internal void InitializeDynamicIsland(MainWindow host)
        {
            _host = host;

            try { IslandCard.Translation = new Vector3(0, 8, 24); } catch { }

            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
                if (File.Exists(iconPath))
                    IslandIcon.Source = new BitmapImage(new Uri(iconPath));
            }
            catch { }

            // 跑马灯视口需要裁剪
            IslandMarqueeViewport.SizeChanged += (_, e) => UpdateClip(IslandMarqueeViewport, e.NewSize.Width, e.NewSize.Height);

            LoadIslandHistory();
            ApplyIslandMetrics();

            // 空闲态：默认隐藏，等主窗口告知“主页面已显示”后再常驻显示纯黑胶囊
            HideIslandWindow();

            try
            {
                File.WriteAllText(IslandLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 灵动岛会话开始，屏幕 {_screenWidth}×{_screenHeight}，" +
                    $"收起 {IslandCollapsedWidth}×{IslandCollapsedHeight}，横幅 {_islandExpandedWidth:F0}×{_islandExpandedHeight:F0}，" +
                    $"历史 {_islandHistorySide:F0}²{Environment.NewLine}");
            }
            catch { }
        }

        private static void UpdateClip(FrameworkElement element, double width, double height)
        {
            try
            {
                element.Clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width), Math.Max(0, height))
                };
            }
            catch { }
        }

        private void ApplyIslandMetrics()
        {
            _islandExpandedWidth = Math.Max(IslandExpandedMinWidth, _screenWidth - IslandExpandedSideMargin);
            _islandExpandedHeight = Math.Max(72, _screenHeight / 8.0);
            if (_islandMode != IslandMode.Banner)
                _islandBannerHeight = _islandExpandedHeight;      // 非横幅态回到基准高度
            _islandHistorySide = Math.Clamp(Math.Min(_screenWidth * 0.92, _screenHeight * 0.62),
                IslandHistoryMinSide, IslandHistoryMaxSide);

            // 横幅正文字号 clamp(24px, 4.5vw, 40px)
            double fontSize = Math.Clamp(_screenWidth * 0.045, 24, 40);
            IslandHeadlineText.FontSize = fontSize;
            IslandMarqueeText.FontSize = fontSize;

            if (_islandSizeTimer == null)   // 动画中不要覆盖逐帧尺寸
            {
                (double w, double h) = CurrentTargetSize();
                IslandSize.Width = w;
                IslandSize.Height = h;
            }

            ApplyIslandRadii();
            ApplyWindowGeometry();
        }

        /// <summary>信息条宽度：适当左右拉长（约 42% 屏宽，最少 380，最多 屏宽−200），高度不变。</summary>
        private double IslandInfoWidth
            => Math.Clamp(_screenWidth * 0.42, 380, Math.Max(420, _screenWidth - 200));

        private (double W, double H) CurrentTargetSize() => _islandMode switch
        {
            IslandMode.Banner => (_islandExpandedWidth, _islandBannerHeight),
            IslandMode.History => (_islandHistorySide, _islandHistorySide),
            IslandMode.Info => (IslandInfoWidth, IslandCollapsedHeight),   // 只拉长、不增高
            _ => (IslandCollapsedWidth, IslandCollapsedHeight)
        };

        /// <summary>圆角：默认 28；卡片较矮时收敛为高度一半（两端半圆）。</summary>
        private void ApplyIslandRadii()
        {
            try
            {
                (_, double h) = CurrentTargetSize();
                double r = Math.Min(IslandCornerRadius, h / 2);
                var corner = new CornerRadius(r);
                IslandCard.CornerRadius = corner;
            }
            catch { }
        }

        // ==================== 模式切换（1.5 秒弹簧） ====================

        private void SwitchIslandMode(IslandMode mode)
        {
            if (_islandMode == mode) return;
            var previous = _islandMode;
            _islandMode = mode;

            // 离开横幅态：高度回到基准，下一条消息再按需加高
            if (mode != IslandMode.Banner)
                _islandBannerHeight = _islandExpandedHeight;

            ShowIslandWindow();                                // 展开/历史都要确保窗口可见（置顶显示）
            (double w, double h) = CurrentTargetSize();

            ApplyIslandRadii();
            AnimateIslandSize(w, h, IslandAnimationMs);        // 展开/收起均为 1.5 秒弹簧
            SetTopChromeFaded(mode != IslandMode.Collapsed);   // 顶部时间/按钮淡出

            ShowIslandContent(mode);
            if (previous != mode) HideIslandContent(previous);
            if (mode == IslandMode.Banner) CascadeIn();          // 横幅内容自上而下错峰入场

            IslandLog($"模式切换：{previous} → {mode}，目标 {w:F0}×{h:F0}");
        }

        private void ShowIslandContent(IslandMode mode)
        {
            FrameworkElement? el = mode switch
            {
                IslandMode.Banner => IslandExpandedContent,
                IslandMode.History => IslandHistoryContent,
                IslandMode.Info => IslandInfoContent,
                _ => null            // 收起态不显示任何内容（只剩纯黑胶囊）
            };
            if (el == null) return;
            el.Visibility = Visibility.Visible;
            FadeTo(el, 0, 1, 260);
        }

        private void HideIslandContent(IslandMode mode)
        {
            FrameworkElement? el = mode switch
            {
                IslandMode.Banner => IslandExpandedContent,
                IslandMode.History => IslandHistoryContent,
                IslandMode.Info => IslandInfoContent,
                _ => null
            };
            if (el == null) return;
            FadeTo(el, 1, 0, 200);

            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            hideTimer.Tick += (_, _) =>
            {
                hideTimer.Stop();
                if (_islandMode != mode) el.Visibility = Visibility.Collapsed;
            };
            hideTimer.Start();
        }

        // ==================== 消息流程 ====================

        /// <summary>轮询到新消息：记入历史、展开横幅提示并滚动，带图片时弹出预览。</summary>
        public void ShowIslandMessages(IEnumerable<IslandMessage> messages)
        {
            var list = messages
                .Select(m => new IslandMessage(
                    m.Sender,
                    string.IsNullOrWhiteSpace(m.Text) ? "收到一条新消息" : m.Text,
                    m.ImageUrl))                                  // 注意：必须带上图片地址，否则不会弹图片
                .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .ToList();
            if (list.Count == 0) return;

            IslandLog($"收到 {list.Count} 条消息：{string.Join(" | ", list.Select(m => Short(m.Text)))}"
                + (list.Any(m => !string.IsNullOrWhiteSpace(m.ImageUrl)) ? "（含图片）" : ""));

            // 历史记录 + 图片预览
            foreach (var m in list)
            {
                string? imageUrl = m.ImageUrl;
                AddIslandHistory(new IslandHistoryEntry
                {
                    Sender = m.Sender,
                    Text = m.Text,
                    Time = DateTime.Now.ToString("MM-dd HH:mm"),
                    ImageUrl = imageUrl
                });
                if (!string.IsNullOrWhiteSpace(imageUrl)) RequestImagePreview(imageUrl);
            }

            _islandMessages.Clear();
            _islandMessages.AddRange(list);
            _islandIndex = 0;

            ShowIslandWindow();                     // 无论主界面是否在前台/是否可见，都先把灵动岛显示出来
            SwitchIslandMode(IslandMode.Banner);
            StartHeadlinePhase();
        }

        private static string Short(string text) => text.Length <= 24 ? text : text.Substring(0, 24) + "…";

        /// <summary>设置页“测试灵动岛”用：用示例消息演示完整流程。</summary>
        public void PreviewDynamicIsland()
        {
            ShowIslandMessages(new[]
            {
                new IslandMessage("测试老师", "这是一条灵动岛测试消息：收到新消息后先提示两秒，再从右向左滚动两遍，然后自动收起。"),
                new IslandMessage("测试老师", "第二条测试消息，用于验证多条消息会依次滚动。")
            });
        }

        private void StartHeadlinePhase()
        {
            StopMarquee();
            _islandPhaseTimer?.Stop();

            IslandSenderPanel.Visibility = Visibility.Collapsed;
            IslandMarqueeViewport.Visibility = Visibility.Collapsed;
            IslandHeadlineText.Visibility = Visibility.Visible;
            IslandHeadlineText.Text = "收到新消息";
            FadeTo(IslandHeadlineText, 0, 1, 220);

            _islandPhaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IslandHeadlineSeconds) };
            _islandPhaseTimer.Tick += (_, _) =>
            {
                _islandPhaseTimer?.Stop();
                PlayMessageAt(0);
            };
            _islandPhaseTimer.Start();
        }

        private void PlayMessageAt(int index)
        {
            if (_islandMode != IslandMode.Banner) return;      // 已被用户切到历史视图
            if (index >= _islandMessages.Count)
            {
                SwitchIslandMode(IslandMode.Collapsed);
                return;
            }

            _islandIndex = index;
            var msg = _islandMessages[index];

            IslandSenderPanel.Visibility = Visibility.Visible;
            IslandSenderText.Text = string.IsNullOrWhiteSpace(msg.Sender) ? "班级通知" : $"来自{msg.Sender}老师";

            IslandHeadlineText.Visibility = Visibility.Collapsed;
            IslandMarqueeViewport.Visibility = Visibility.Visible;
            IslandMarqueeText.Text = msg.Text;
            IslandMarqueeTransform.X = 0;

            ShowMessageBody(index);
        }

        /// <summary>
        /// 统一采用**单行横向滚动**（字号始终不变 = clamp(4.5vw, 24, 40)，横幅始终 1/8 屏高）：
        /// 文字按实测宽度整段渲染，从可视区右边缘之外进入、向左完全移出，共滚动两遍。
        /// 长消息只是距离更长、时长更长，**每一段都会完整经过可视区**，不会被截断。
        /// </summary>
        private void ShowMessageBody(int index)
        {
            StopMarquee();

            double baseFont = Math.Clamp(_screenWidth * 0.045, 24, 40);   // 字体固定，不随消息长短变化
            EnsureBannerHeight(Math.Max(72, _islandExpandedHeight));      // 横幅保持基准高度（1/8 屏高）
            ConfigureMarqueeText(baseFont);                               // 单行、不换行、不截断

            StartMarquee(index, BannerContentWidth());
        }

        /// <summary>
        /// 横幅正文可视区宽度：卡片宽 − 外边距(12×2) − 推送卡内边距(12×2)。
        /// **不能读 ActualWidth**：收起态（胶囊仅约 150–240px）刚切回横幅时布局尚未刷新，
        /// 会读到旧的小尺寸，导致可视窗口只有一两百像素（表现为文字中间截断 + 大段空白）。
        /// </summary>
        private double BannerContentWidth() => Math.Max(200, _islandExpandedWidth - 48);

        /// <summary>横幅正文可视区高度：卡片高 − 外边距(10×2) − 内边距(8×2) − 来源行(26+4)。</summary>
        private double BannerContentHeight() => Math.Max(48, _islandBannerHeight - 66);

        /// <summary>把横幅高度调整到指定值（同一弹簧动画）。</summary>
        private void EnsureBannerHeight(double height)
        {
            if (Math.Abs(_islandBannerHeight - height) < 1) return;
            _islandBannerHeight = height;
            if (_islandMode == IslandMode.Banner)
                AnimateIslandSize(_islandExpandedWidth, _islandBannerHeight, 700);
            ApplyIslandRadii();
        }

        /// <summary>横幅正文可视区高度（仅用于日志/兜底）。</summary>
        private double ContentViewportHeight() => BannerContentHeight();

        /// <summary>滚动模式：单行、不换行、不限行数、指定字号。</summary>
        private void ConfigureMarqueeText(double font)
        {
            try
            {
                IslandMarqueeText.TextWrapping = TextWrapping.NoWrap;
                IslandMarqueeText.MaxLines = 0;
                IslandMarqueeText.TextTrimming = TextTrimming.None;
                IslandMarqueeText.FontSize = font;
                IslandMarqueeText.LineHeight = Math.Floor(font * 1.15);
                IslandMarqueeText.Width = double.NaN;    // Canvas 宿主：按自然宽度排版，不要固定宽度
                IslandMarqueeText.Height = double.NaN;
                Canvas.SetLeft(IslandMarqueeText, 0);
                // 可视区恢复自适应（不设固定宽高）
                IslandMarqueeViewport.Width = double.NaN;
                IslandMarqueeViewport.Height = double.NaN;
            }
            catch { }
        }

        /// <summary>换行模式：按指定宽度自动换行、不限制行数、不截断；返回换行后文字总高度。</summary>
        private double ConfigureWrappedText(double font, double width, bool topAlign)
        {
            try
            {
                IslandMarqueeText.TextWrapping = TextWrapping.Wrap;
                IslandMarqueeText.MaxLines = 0;
                IslandMarqueeText.TextTrimming = TextTrimming.None;
                IslandMarqueeText.FontSize = font;
                IslandMarqueeText.LineHeight = Math.Floor(font * 1.15);
                IslandMarqueeText.HorizontalAlignment = HorizontalAlignment.Left;
                IslandMarqueeText.VerticalAlignment = topAlign ? VerticalAlignment.Top : VerticalAlignment.Center;
                IslandMarqueeText.Width = Math.Max(80, width);
                IslandMarqueeText.Height = double.NaN;      // 先清除旧的固定高度，保证测量到完整换行高度
                IslandMarqueeTransform.X = 0;

                IslandMarqueeText.Measure(new Windows.Foundation.Size(Math.Max(80, width), double.PositiveInfinity));
                return IslandMarqueeText.DesiredSize.Height;
            }
            catch { return 0; }
        }

        private double MeasureSingleLineWidth()
        {
            try
            {
                IslandMarqueeText.Width = double.NaN;
                IslandMarqueeText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return IslandMarqueeText.DesiredSize.Width;
            }
            catch { return 0; }
        }

        private void StartMarquee(int index, double viewport)
        {
            StopMarquee();

            // 可视区：用“卡片尺寸推算值”明确设定窗口大小（不读 Actual，避免收起态遗留的小尺寸），
            // 并设为左对齐，保证窗口起点与内容左边缘对齐
            double viewportHeight = BannerContentHeight();
            try
            {
                IslandMarqueeViewport.Width = viewport;
                IslandMarqueeViewport.Height = viewportHeight;
                IslandMarqueeViewport.HorizontalAlignment = HorizontalAlignment.Left;
            }
            catch { }
            UpdateClip(IslandMarqueeViewport, viewport, viewportHeight);
            try { IslandMarqueeViewport.UpdateLayout(); } catch { }

            // 测量整段文字宽度（Canvas 宿主 → 无限宽测量，得到真实宽度用于计算滚动距离）
            string text = IslandMarqueeText.Text ?? "";
            double fontSize = IslandMarqueeText.FontSize > 0 ? IslandMarqueeText.FontSize : 24;
            double lineHeight = Math.Max(20, Math.Floor(fontSize * 1.15));
            double textWidth = 0;
            try
            {
                IslandMarqueeText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                textWidth = IslandMarqueeText.DesiredSize.Width;
            }
            catch { }
            if (textWidth <= 1)
                textWidth = Math.Max(40, text.Length * fontSize);   // 兜底估算（汉字约等于一个字号宽）

            // 单行在可视区内垂直居中
            try { Canvas.SetTop(IslandMarqueeText, Math.Max(0, (viewportHeight - lineHeight) / 2)); } catch { }

            const double gap = 30;
            double from = viewport + gap;      // 从可视区右侧之外进入（起始位置紧贴右边缘）
            double to = -textWidth;            // 完全移出左侧
            double distance = from - to;
            double speed = Math.Max(190, viewport * 0.14);
            double seconds = Math.Clamp(distance / speed, 4, 40);   // 长消息给足时间（最多 40 秒/遍）

            IslandLog($"横向滚动第 {index + 1} 条：{text.Length} 字，视口 {viewport:F0}，文字宽 {textWidth:F0}，" +
                      $"字号 {fontSize:F0}，速度 {(distance / seconds):F0}px/s，单遍 {seconds:F1}s，共两遍");

            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                BeginTime = TimeSpan.FromSeconds(IslandMarqueeDelaySec),
                Duration = TimeSpan.FromSeconds(seconds),
                RepeatBehavior = new RepeatBehavior(IslandMarqueeLoops)
            };
            Storyboard.SetTarget(anim, IslandMarqueeTransform);
            Storyboard.SetTargetProperty(anim, "X");

            _islandMarqueeStoryboard = new Storyboard();
            _islandMarqueeStoryboard.Children.Add(anim);
            _islandMarqueeStoryboard.Completed += (_, _) =>
            {
                IslandMarqueeTransform.X = 0;
                PlayMessageAt(index + 1);
            };

            IslandMarqueeTransform.X = from;
            IslandMarqueeTransform.Y = 0;
            _islandMarqueeStoryboard.Begin();
        }

        private void StopMarquee()
        {
            try { _islandMarqueeStoryboard?.Stop(); } catch { }
            _islandMarqueeStoryboard = null;
            try
            {
                IslandMarqueeTransform.X = 0;
                IslandMarqueeTransform.Y = 0;
            }
            catch { }
        }

        // ==================== 点击 → 方形历史视图 ====================

        private void IslandCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource)) return;      // 点内部的按钮不触发切换
            ToggleIslandHistory();
        }

        private void IslandHistoryClose_Click(object sender, RoutedEventArgs e)
            => SwitchIslandMode(IslandMode.Collapsed);


        /// <summary>方形历史视图的标题栏：点标题栏（非按钮）收起。</summary>
        private void IslandHistoryHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource)) return;
            e.Handled = true;
            SwitchIslandMode(IslandMode.Collapsed);
        }

        /// <summary>历史列表内的点击不要冒泡到卡片（避免误收起）。</summary>
        private void IslandHistoryList_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

        private void ToggleIslandHistory()
        {
            if (_islandMode == IslandMode.History)
            {
                SwitchIslandMode(IslandMode.Collapsed);
                return;
            }

            // 打断正在播放的消息滚动，切到历史
            _islandPhaseTimer?.Stop();
            StopMarquee();
            RenderIslandHistory();
            SwitchIslandMode(IslandMode.History);
        }

        // ==================== 主页面常驻（纯黑胶囊） ====================

        /// <summary>
        /// 由主窗口告知“主页面（第一屏）是否正在显示”。
        /// 显示时灵动岛**常驻**为纯黑胶囊（无任何内容与图案，保留原版宽度）；不显示时隐藏窗口。
        /// 收到消息时仍会置顶展开为横幅，播完后再回到常驻胶囊。
        /// </summary>
        internal void SetHomeVisible(bool visible)
        {
            if (_homeVisible == visible) return;
            _homeVisible = visible;
            UpdateIdleVisibility();
        }

        /// <summary>空闲态（收起态）可见性：主页显示 → 常驻胶囊；否则隐藏。</summary>
        private void UpdateIdleVisibility()
        {
            if (_islandMode != IslandMode.Collapsed) return;   // 展开/历史态由各自流程控制
            if (_homeVisible) ShowPill();
            else HideIslandWindow();
        }

        /// <summary>常驻胶囊：窗口以收起尺寸显示，不显示任何内容与图案（纯黑圆角，无白边）。</summary>
        private void ShowPill()
        {
            try
            {
                IslandExpandedContent.Visibility = Visibility.Collapsed;
                IslandHistoryContent.Visibility = Visibility.Collapsed;
                StopMarquee();
                _islandPhaseTimer?.Stop();

                IslandSize.Width = IslandCollapsedWidth;
                IslandSize.Height = IslandCollapsedHeight;
                ApplyIslandRadii();
                ShowIslandWindow();
            }
            catch { }
        }

        /// <summary>横幅/历史结束后回到正确的静止状态：信息条 → 信息条，否则 → 常驻胶囊（软件不可见时隐藏）。</summary>
        private void LeaveBannerToIdle()
        {
            if (_infoActive)
            {
                ShowIslandWindow();
                SwitchIslandMode(IslandMode.Info);
                return;
            }
            if (_homeVisible) ShowPill();
            else HideIslandWindow();
        }

        // ==================== 信息条（点歌播放中 / 打铃中） ====================

        /// <summary>
        /// 显示/更新信息条：灵动岛左右拉长、高度不变，左侧标题、右侧内容（时间或任务名）。
        /// 消息横幅/历史视图优先，它们结束时会自动回到信息条（见 LeaveBannerToIdle）。
        /// </summary>
        internal void ShowInfo(string left, string right)
        {
            try
            {
                _infoActive = true;
                _infoLeft = left ?? "";
                _infoRight = right ?? "";

                IslandInfoLeftText.Text = _infoLeft;
                IslandInfoRightText.Text = _infoRight;

                // 消息横幅/历史视图正在显示时不打断，等它结束时再切到信息条
                if (_islandMode == IslandMode.Banner || _islandMode == IslandMode.History) return;

                if (_islandMode == IslandMode.Info)
                {
                    ApplyWindowGeometry();     // 文案变了，位置/裁剪区域重算一次即可
                    return;
                }

                ShowIslandWindow();
                SwitchIslandMode(IslandMode.Info);
            }
            catch { }
        }

        /// <summary>隐藏信息条：回到常驻纯黑胶囊（软件不可见时整体隐藏）。</summary>
        internal void HideInfo()
        {
            try
            {
                _infoActive = false;
                _infoLeft = "";
                _infoRight = "";
                if (_islandMode != IslandMode.Info) return;
                SwitchIslandMode(IslandMode.Collapsed);   // 收缩动画结束后自动回常驻胶囊/隐藏
            }
            catch { }
        }
        /// <summary>供主窗口/桌面页调用：显示（或收起）历史消息视图（窗口置顶弹出）。</summary>
        public void ToggleHistory()
        {
            try
            {
                if (_islandMode == IslandMode.History)
                {
                    SwitchIslandMode(IslandMode.Collapsed);
                    return;
                }
                ShowIslandWindow();
                ToggleIslandHistory();
            }
            catch { }
        }

        private static bool IsInsideButton(object? source)
        {
            var el = source as DependencyObject;
            while (el != null)
            {
                if (el is Button) return true;
                el = VisualTreeHelper.GetParent(el);
            }
            return false;
        }

        // ==================== 历史消息（持久化） ====================

        private static string IslandHistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "islandHistory.json");

        private void AddIslandHistory(IslandHistoryEntry entry)
        {
            try
            {
                _islandHistory.Insert(0, entry);
                while (_islandHistory.Count > IslandHistoryMax) _islandHistory.RemoveAt(_islandHistory.Count - 1);
                SaveIslandHistory();
            }
            catch { }
        }

        private void LoadIslandHistory()
        {
            try
            {
                if (!File.Exists(IslandHistoryPath)) return;
                var list = JsonSerializer.Deserialize<List<IslandHistoryEntry>>(File.ReadAllText(IslandHistoryPath));
                if (list == null) return;
                _islandHistory.Clear();
                _islandHistory.AddRange(list.Take(IslandHistoryMax));
            }
            catch { }
        }

        private void SaveIslandHistory()
        {
            try
            {
                string? dir = Path.GetDirectoryName(IslandHistoryPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(IslandHistoryPath, JsonSerializer.Serialize(_islandHistory));
            }
            catch { }
        }

        /// <summary>方形历史视图：正常字号列出历史消息（含图片提示，可再次点开图片）。</summary>
        private void RenderIslandHistory()
        {
            try
            {
                IslandHistoryList.Children.Clear();

                if (_islandHistory.Count == 0)
                {
                    IslandHistoryList.Children.Add(new TextBlock
                    {
                        Text = "暂无历史消息",
                        FontSize = 15,
                        Foreground = (Brush)IslandRoot.Resources["IslandTextSecondaryBrush"],
                        Margin = new Thickness(2, 8, 0, 0)
                    });
                    return;
                }

                foreach (var item in _islandHistory)
                {
                    var panel = new StackPanel { Spacing = 4 };

                    var head = new Grid();
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var sender = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(item.Sender) ? "班级通知" : $"来自{item.Sender}老师",
                        FontSize = 13,
                        Foreground = (Brush)IslandRoot.Resources["IslandTextSecondaryBrush"],
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var time = new TextBlock
                    {
                        Text = item.Time,
                        FontSize = 12,
                        Foreground = (Brush)IslandRoot.Resources["IslandTextSecondaryBrush"],
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(time, 1);
                    head.Children.Add(sender);
                    head.Children.Add(time);
                    panel.Children.Add(head);

                    panel.Children.Add(new TextBlock
                    {
                        Text = item.Text,
                        FontSize = 15,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)IslandRoot.Resources["IslandTextPrimaryBrush"]
                    });

                    if (!string.IsNullOrWhiteSpace(item.ImageUrl))
                    {
                        var imageUrl = item.ImageUrl;
                        var imageBtn = new Button
                        {
                            Content = "查看图片",
                            FontSize = 12,
                            Padding = new Thickness(10, 3, 10, 3),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        imageBtn.Click += (_, _) => RequestImagePreview(imageUrl);
                        panel.Children.Add(imageBtn);
                    }

                    IslandHistoryList.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = panel
                    });
                }
            }
            catch (Exception ex)
            {
                IslandLog($"历史渲染失败：{ex.Message}");
            }
        }

        // ==================== 逐帧弹簧形变 ====================

        private void AnimateIslandSize(double width, double height, double durationMs)
        {
            StopIslandSizeAnimation();

            _springFromW = IslandSize.Width;
            _springFromH = IslandSize.Height;
            _springToW = width;
            _springToH = height;
            _springDurationMs = durationMs;
            _springMaxW = _springFromW;
            _springMaxH = _springFromH;
            _springStart = DateTime.UtcNow;

            _islandSizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SpringTickMs) };
            _islandSizeTimer.Tick += (_, _) => TickIslandSize();
            _islandSizeTimer.Start();

            TickIslandSize();
        }

        private void TickIslandSize()
        {
            double elapsed = (DateTime.UtcNow - _springStart).TotalMilliseconds;
            double t = Math.Clamp(elapsed / Math.Max(1, _springDurationMs), 0, 1);

            double w = _springFromW + (_springToW - _springFromW) * SpringValue(t, SpringDampingWidth);
            double h = _springFromH + (_springToH - _springFromH) * SpringValue(t, SpringDampingHeight);

            w = Math.Clamp(w, 110, Math.Max(120, _screenWidth - 8));
            h = Math.Clamp(h, 30, Math.Max(80, _screenHeight));

            try
            {
                IslandSize.Width = w;
                IslandSize.Height = h;
            }
            catch { }
            ApplyWindowGeometry();      // 窗口大小与裁剪区域跟着弹簧逐帧变化

            _springMaxW = Math.Max(_springMaxW, w);
            _springMaxH = Math.Max(_springMaxH, h);

            if (t >= 1)
            {
                try
                {
                    IslandSize.Width = _springToW;
                    IslandSize.Height = _springToH;
                }
                catch { }
                ApplyWindowGeometry();
                IslandLog($"形变完成：{_springToW:F0}×{_springToH:F0}（峰值 {_springMaxW:F0}×{_springMaxH:F0}）");
                StopIslandSizeAnimation();

                // 收起动画结束 → 消息已显示完成：主页面显示时常驻纯黑胶囊，否则隐藏窗口
                if (_islandMode == IslandMode.Collapsed) LeaveBannerToIdle();
            }
        }

        private void StopIslandSizeAnimation()
        {
            try { _islandSizeTimer?.Stop(); } catch { }
            _islandSizeTimer = null;
        }

        private static double SpringValue(double t, double damping)
        {
            double omega0 = Math.Sqrt(SpringStiffness / SpringMass);
            double zeta = damping / (2 * Math.Sqrt(SpringStiffness * SpringMass));
            double omegaD = omega0 * Math.Sqrt(Math.Max(0.0001, 1 - zeta * zeta));
            double tt = Math.Clamp(t, 0, 1) * SpringTimeScale;   // 让振荡在动画时长内刚好收敛
            double decay = Math.Exp(-zeta * omega0 * tt);
            return 1 - decay * (Math.Cos(omegaD * tt) + (zeta * omega0 / omegaD) * Math.Sin(omegaD * tt));
        }


        private void CascadeIn()
        {
            PlayBlockIn(IslandSenderPanel, IslandSenderTransform, 0);
            PlayBlockIn(IslandContentView, IslandContentTransform, 1);
        }

        private void PlayBlockIn(UIElement element, TranslateTransform transform, int index)
        {
            try
            {
                element.Opacity = 0;
                transform.Y = CascadeOffsetY;

                double delay = CascadeDelayBaseMs + index * CascadeDelayStepMs;
                var sb = new Storyboard();

                var fade = new DoubleAnimation
                {
                    To = 1,
                    BeginTime = TimeSpan.FromMilliseconds(delay),
                    Duration = TimeSpan.FromMilliseconds(CascadeFadeMs),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, element);
                Storyboard.SetTargetProperty(fade, "Opacity");

                var move = new DoubleAnimation
                {
                    To = 0,
                    BeginTime = TimeSpan.FromMilliseconds(delay),
                    Duration = TimeSpan.FromMilliseconds(CascadeMoveMs),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(move, transform);
                Storyboard.SetTargetProperty(move, "Y");

                sb.Children.Add(fade);
                sb.Children.Add(move);
                sb.Begin();
            }
            catch { }
        }

        private static void FadeTo(UIElement target, double from, double to, double durationMs, double beginMs = 0)
        {
            try
            {
                var anim = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    BeginTime = TimeSpan.FromMilliseconds(beginMs),
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, target);
                Storyboard.SetTargetProperty(anim, "Opacity");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch { }
        }

        /// <summary>展开时把主界面顶部（桌面大字时间、✕、更多功能）淡出，收起后淡入。</summary>
        private void SetTopChromeFaded(bool faded)
        {
            try { _host?.OnIslandExpandedChanged(faded); } catch { }
        }

        /// <summary>请求主窗口弹出图片预览（灵动岛是独立窗口，预览仍在主界面里显示）。</summary>
        private void RequestImagePreview(string imageUrl)
        {
            try { _host?.RequestImagePreview(imageUrl); } catch { }
        }
    }

    /// <summary>灵动岛显示的一条消息（ImageUrl 非空时同时弹出图片预览）。</summary>
    public sealed class IslandMessage
    {
        public string Sender { get; set; } = "";
        public string Text { get; set; } = "";
        public string? ImageUrl { get; set; }

        public IslandMessage() { }
        public IslandMessage(string sender, string text, string? imageUrl = null)
        {
            Sender = sender ?? "";
            Text = text ?? "";
            ImageUrl = imageUrl;
        }
    }

    /// <summary>历史消息条目（持久化到 islandHistory.json）。</summary>
    public sealed class IslandHistoryEntry
    {
        public string Sender { get; set; } = "";
        public string Text { get; set; } = "";
        public string Time { get; set; } = "";
        public string? ImageUrl { get; set; }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace SmartClassNight
{
    public sealed partial class MainWindow : Window
    {
        public const string CurrentVersion = "1.2.0";
        public const string UpdateUrl = "https://f18.llt-service.cn/update.txt";
        public const string DownloadUrl = "https://f18.llt-service.cn/download";
        public const string FeedbackUrl = "https://f18.llt-service.cn/index-app.html";

        /// <summary>“此电脑”在 Shell 命名空间中的 CLSID 路径；explorer.exe 直接打开即为“此电脑”首页。</summary>
        public const string ThisPcPath = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        private DispatcherTimer? _clockTimer;
        private DispatcherTimer? _taskbarRefreshTimer;
        private DispatcherTimer? _hideTaskbarTimer;
        private DispatcherTimer? _splashTimer;
        private const int SplashVisibleMs = 4300;
        private bool _desktopSwitched = false;

        private ObservableCollection<AppItem> _apps = new();
        public ObservableCollection<AppItem> Apps => _apps;

        private bool _isPreviewEnabled = false;
        private string _previewDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "preview.json");
        private readonly string _appOrderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "appOrder.json");

        public ObservableCollection<WindowInfoItem> TaskbarWindows { get; } = new();

        private ImageBrush? _smartClassBackgroundBrush;
        private bool _dpiBound = false;

        public MainWindow()
        {
            this.InitializeComponent();
            AppHost.Main = this;

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            RootGrid.PointerMoved += OnRootGridPointerMoved;

            LoadPreviewSetting();

            _smartClassBackgroundBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
            SmartClassGrid.Background = _smartClassBackgroundBrush;

            TaskbarAppsList.ItemsSource = TaskbarWindows;

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += UpdateClock;
            _clockTimer.Start();

            _taskbarRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _taskbarRefreshTimer.Tick += (_, _) => RefreshTaskbarApps();
            _taskbarRefreshTimer.Start();

            _hideTaskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _hideTaskbarTimer.Tick += HideTaskbarTimer_Tick;

            ContentFrame.Navigate(typeof(DesktopPage), this);
            UpdateDndVisual();          // 勿扰按钮图标与配置保持一致

            // iOS 风格灵动岛：独立置顶窗口（无论前台是什么程序都能显示）
            _islandWindow = new IslandWindow();
            _islandWindow.Attach(this, RootGrid.ActualWidth, RootGrid.ActualHeight);
            _islandWindow.InitializeDynamicIsland(this);
            RootGrid.SizeChanged += (_, _) =>
                _islandWindow?.SetScreenSize(RootGrid.ActualWidth, RootGrid.ActualHeight);

            // 校园自动打铃（全局定时器，独立于当前显示哪一屏）
            ClassDataService.DndChanged += _ => { UpdateDndVisual(); FlushPendingAlerts(); };   // 勿扰关闭后补显示消息
            BellService.Start();

            StartupSplash.Loaded += StartupSplash_Loaded;
            RootGrid.Loaded += RootGrid_Loaded;
        }

        private IslandWindow? _islandWindow;
        private int _desktopScreenIndex;          // 当前显示的屏（0 = 主页面）
        private bool _islandResidentState;        // 上次同步给灵动岛的“常驻”状态
        private readonly System.Collections.Generic.List<AnnouncementMessage> _pendingAlerts = new();  // 被抑制、待补显示的消息
        private bool _wasSuppressing;             // 上一次检查时是否处于抑制状态

        /// <summary>诊断日志（写入 %LOCALAPPDATA%\金华一中科技校园套件\island.log），转发到灵动岛实现。</summary>
        internal static void IslandLog(string message) => IslandWindow.IslandLog(message);

        /// <summary>主窗口实例（供静态服务类调用，等价于 AppHost.Main）。</summary>
        internal static MainWindow? AppHostMain => AppHost.Main;

        /// <summary>主界面宽度（DIP），供图片预览等按屏幕尺寸排版。</summary>
        public double ScreenWidth => RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1920;

        /// <summary>主界面高度（DIP）。</summary>
        public double ScreenHeight => RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 1080;

        /// <summary>灵动岛当前是否展开（供桌面页同步顶部元素淡出）。</summary>
        public bool IsIslandExpanded => _islandWindow?.IsIslandExpanded ?? false;

        /// <summary>灵动岛展开/收起时同步主界面顶部元素的淡出。</summary>
        public void OnIslandExpandedChanged(bool faded)
        {
            try
            {
                if (ContentFrame.Content is DesktopPage page) page.SetClockFaded(faded);
            }
            catch { }

            try
            {
                FadeElement(ExitButton, faded ? 0 : 1, 260);
                ExitButton.IsHitTestVisible = !faded;
                FadeElement(MoreFeaturesButton, faded ? 0 : 1, 260);
                MoreFeaturesButton.IsHitTestVisible = !faded;
            }
            catch { }
        }

        private static void FadeElement(UIElement element, double to, double durationMs)
        {
            try
            {
                var anim = new DoubleAnimation
                {
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, element);
                Storyboard.SetTargetProperty(anim, "Opacity");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch { }
        }

        /// <summary>灵动岛请求显示图片预览（预览仍在主界面居中弹出）。</summary>
        public void RequestImagePreview(string imageUrl) => ShowImagePreview(imageUrl);

        /// <summary>点歌播放中：灵动岛左右拉长显示「歌名 | 进度时间」。</summary>
        public void ShowIslandMusicInfo(string title, string timeText)
        {
            try { _islandWindow?.ShowMusicInfo(title, timeText); } catch { }
        }

        /// <summary>点歌停止/暂停：恢复灵动岛原状。</summary>
        public void ClearIslandMusicInfo()
        {
            try { _islandWindow?.HideInfoBar(); } catch { }
        }

        /// <summary>打铃中：灵动岛左右拉长显示「正在播放铃声 | 任务名」。</summary>
        public void ShowIslandBellInfo(string taskName)
        {
            try { _islandWindow?.ShowBellInfo(taskName); } catch { }
        }

        /// <summary>打铃结束：恢复灵动岛原状。</summary>
        public void ClearIslandBellInfo()
        {
            try { _islandWindow?.HideInfoBar(); } catch { }
        }
        /// <summary>设置页「预览灵动岛」入口。</summary>
        public void PreviewDynamicIsland() => _islandWindow?.PreviewDynamicIsland();

        private bool _startupUpdateChecked = false;
        private bool _pluginsStarted = false;
        private DispatcherTimer? _notificationTimer;

        /// <summary>启动插件（仅一次）：不依赖启动视频是否播放，保证随系统启动。</summary>
        private void StartPluginsOnce()
        {
            if (_pluginsStarted) return;
            _pluginsStarted = true;
            PluginRunner.RunStartupPlugins();
        }

        private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (_startupUpdateChecked) return;
            _startupUpdateChecked = true;
            StartPluginsOnce();   // 插件随系统启动（独立于启动视频）

            // 教师通知轮询：api.php?action=get_announcements&class_id=… 每 10 秒一次
            // 班级 id 来自主页“消息提醒班级选择”（本地保存），回退到“更多功能”WebView 的 localStorage
            NotificationService.Initialize();
            ClassDataService.Load();
            _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _notificationTimer.Tick += async (_, _) => await PollNotificationsAsync();
            _notificationTimer.Start();

            // 启动后先查一次，之后交给 10 秒轮询
            var firstPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            firstPoll.Tick += async (_, _) =>
            {
                firstPoll.Stop();
                await PollNotificationsAsync();
            };
            firstPoll.Start();

            StartUsbAndExplorer();   // 检测到插入 U 盘时仅打开该 U 盘
            IslandLog("启动参数：" + string.Join(" | ", Environment.GetCommandLineArgs()));
            StartIslandTestIfRequested();
            StartAddSongTestIfRequested();
            StartDiagnosticsIfRequested();
            await CheckForUpdatesAtStartupAsync();
        }

        /// <summary>诊断用：启动参数 /addsong="路径" 会在启动后把该音频加入点歌列表（不弹文件对话框）。</summary>
        private void StartAddSongTestIfRequested()
        {
            try
            {
                string? path = null;
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg.StartsWith("/addsong=", StringComparison.OrdinalIgnoreCase))
                    {
                        path = arg.Substring("/addsong=".Length).Trim().Trim('"');
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(path)) return;
                IslandLog($"诊断 /addsong：已登记 {path}");

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        IslandLog($"诊断 /addsong：触发，内容 = {ContentFrame.Content?.GetType().Name ?? "null"}");
                        if (ContentFrame.Content is DesktopPage page)
                        {
                            int added = await page.AddSongsAsync(new[] { path! });
                            IslandLog($"诊断 /addsong：加入 {added} 首（{path}）");
                        }
                    }
                    catch (Exception ex)
                    {
                        IslandLog($"诊断 /addsong 失败：{ex.Message}");
                    }
                };
                timer.Start();
            }
            catch { }
        }

        /// <summary>诊断用：启动参数 /islandtest 会在启动后自动演示一次灵动岛（便于截图核对）。</summary>
        private void StartIslandTestIfRequested()
        {
            try
            {
                if (!Environment.GetCommandLineArgs().Any(a => a.Equals("/islandtest", StringComparison.OrdinalIgnoreCase)))
                    return;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _islandWindow?.PreviewDynamicIsland();
                };
                timer.Start();

                // 第二次演示：便于“主界面在前台时灵动岛可见性”的现场核对
                var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
                again.Tick += (_, _) =>
                {
                    again.Stop();
                    _islandWindow?.PreviewDynamicIsland();
                };
                again.Start();
            }
            catch { }
        }

        /// <summary>
        /// 诊断开关（现场排查/自测用）：
        /// - `/dndtest`：启动时开启勿扰，25 秒后自动关闭（用于验证“抑制 → 补显示未显示过的消息”）；
        /// - `/b64test=图片路径`：把本地图片转成 base64 data URI 走一遍图片预览（验证 base64 解码）。
        /// </summary>
        private void StartDiagnosticsIfRequested()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();

                if (args.Any(a => a.Equals("/dndtest", StringComparison.OrdinalIgnoreCase)))
                {
                    ClassDataService.SetDnd(true);
                    UpdateDndVisual();
                    IslandLog("诊断 /dndtest：勿扰模式已开启（25 秒后自动关闭并补显示消息）");
                    var off = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                    off.Tick += (_, _) =>
                    {
                        off.Stop();
                        ClassDataService.SetDnd(false);
                        UpdateDndVisual();
                        IslandLog("诊断 /dndtest：勿扰模式已关闭");
                    };
                    off.Start();
                }

                string? b64Path = null;
                foreach (var a in args)
                    if (a.StartsWith("/b64test=", StringComparison.OrdinalIgnoreCase))
                        b64Path = a.Substring("/b64test=".Length).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(b64Path) && File.Exists(b64Path))
                {
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    t2.Tick += (_, _) =>
                    {
                        t2.Stop();
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(b64Path!);
                            string ext = Path.GetExtension(b64Path!).ToLowerInvariant() switch
                            {
                                ".jpg" or ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".bmp" => "image/bmp",
                                _ => "image/png"
                            };
                            string dataUri = $"data:{ext};base64,{Convert.ToBase64String(bytes)}";
                            IslandLog($"诊断 /b64test：data URI 长度 {dataUri.Length}，开始预览");
                            ShowImagePreview(dataUri);
                        }
                        catch (Exception ex) { IslandLog($"诊断 /b64test 失败：{ex.Message}"); }
                    };
                    t2.Start();
                }
            }
            catch { }
        }
        /// <summary>供桌面页调用：切换班级后立即轮询一次新消息。</summary>
        public async Task PollNotificationsNowAsync()
        {
            ClassDataService.Load();
            await PollNotificationsAsync();
        }

        /// <summary>
        /// 轮询一次班级公告：
        /// - 始终更新桌面页「最近消息」卡片与灵动岛历史；
        /// - **消息提醒（灵动岛横幅 + 通知中心 Toast + 提示音）**受三种情况抑制：
        ///   ① 勿扰模式开启；② 前台是**别的程序的全屏窗口**；③ 停留在主页面且开启了「主页面隐藏消息提醒」。
        ///   其中 ① ② 会把消息记入“未显示队列”，等条件解除（关勿扰 / 全屏应用退出）后自动补显示。
        /// </summary>
        private async Task PollNotificationsAsync()
        {
            try
            {
                int? classId = await GetClassIdFromWebViewAsync();
                var newMessages = await NotificationService.PollAsync(classId);
                if (newMessages.Count == 0) return;

                // 桌面页“最近消息”与通知中心始终保留（不受勿扰影响），保证信息不丢
                ShowLatestMessageOnDesktop(newMessages);
                foreach (var m in newMessages)
                    NotificationService.ShowToast(
                        string.IsNullOrWhiteSpace(m.Sender) ? "班级通知" : $"来自{m.Sender}老师",
                        m.Content.Length > 120 ? m.Content.Substring(0, 120) + "…" : m.Content);

                if (ShouldSuppressAlert(out string reason, out bool deferrable))
                {
                    IslandLog($"消息提醒已抑制（{reason}）：{newMessages.Count} 条"
                        + (deferrable ? "，已记入未显示队列" : ""));
                    if (deferrable) _pendingAlerts.AddRange(newMessages);
                    return;
                }

                ShowAlerts(newMessages);
            }
            catch { }
        }

        /// <summary>把消息推给灵动岛 + 播放提示音。</summary>
        private void ShowAlerts(System.Collections.Generic.List<AnnouncementMessage> messages)
        {
            try
            {
                if (messages == null || messages.Count == 0) return;
                NotificationSound.Play();
                _islandWindow?.ShowIslandMessages(messages.Select(m => new IslandMessage(m.Sender, m.Content, m.ImageUrl)));
            }
            catch { }
        }

        /// <summary>
        /// 判断当前是否应抑制消息提醒。
        /// <paramref name="deferrable"/> = 条件解除后应补显示（勿扰模式 / 前台全屏应用）；
        /// 主页面隐藏属于“就地不提醒”（最近消息卡片已经能看到），不进队列。
        /// </summary>
        private bool ShouldSuppressAlert(out string reason, out bool deferrable)
        {
            deferrable = false;
            if (IsHardSuppressed(out reason))
            {
                deferrable = true;      // 勿扰 / 前台全屏：条件解除后要补显示
                return true;
            }

            try
            {
                if (ClassDataService.HideAlertOnHome && IsOnHomeScreen())
                {
                    reason = "主页面隐藏消息提醒";   // 主页面右上角“最近消息”已经能看到，不进补显示队列
                    return true;
                }
            }
            catch { }
            reason = "";
            return false;
        }

        /// <summary>
        /// “硬”抑制条件：勿扰模式开启、或前台是别的程序的全屏窗口。
        /// 这两条解除后必须把未显示过的消息补显示出来（**不受“主页面隐藏”规则影响**）。
        /// </summary>
        private bool IsHardSuppressed(out string reason)
        {
            reason = "";
            try
            {
                if (ClassDataService.DndEnabled) { reason = "勿扰模式"; return true; }
                if (NativeMethods.IsForegroundOtherFullscreen()) { reason = "前台有其他全屏窗口"; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>当前是否停留在主页面（第一屏且桌面层可见）。</summary>
        private bool IsOnHomeScreen()
        {
            try
            {
                if (_desktopScreenIndex != 0) return false;
                if (SmartClassGrid.Visibility != Visibility.Visible) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>条件解除后补显示“未显示过”的消息（勿扰关闭 / 全屏应用退出时调用）。</summary>
        private void FlushPendingAlerts()
        {
            try
            {
                if (_pendingAlerts.Count == 0) return;
                if (IsHardSuppressed(out _)) return;                // 仍处于硬抑制（勿扰/全屏）时不补显示
                var pending = _pendingAlerts.ToList();
                _pendingAlerts.Clear();
                IslandLog($"补显示未显示过的消息：{pending.Count} 条");
                ShowAlerts(pending);
            }
            catch { }
        }

        /// <summary>把最新一条消息显示到桌面页右下角（大字）。</summary>
        private void ShowLatestMessageOnDesktop(System.Collections.Generic.List<AnnouncementMessage> messages)
        {
            try
            {
                var latest = messages.OrderByDescending(m => m.Id).FirstOrDefault();
                if (latest == null) return;
                if (ContentFrame.Content is DesktopPage page)
                    page.SetLatestMessage(latest.Sender, latest.Content, DateTime.Now);
            }
            catch { }
        }

        /// <summary>
        /// 每秒检查一次：之前被抑制的消息提醒是否可以补显示了
        /// （勿扰模式关闭、或前台的全屏应用退出后立即补显示）。
        /// </summary>
        private void CheckPendingAlerts()
        {
            try
            {
                if (_pendingAlerts.Count == 0) { _wasSuppressing = false; return; }

                bool suppressing = IsHardSuppressed(out _);
                if (!suppressing) FlushPendingAlerts();          // 条件已解除（勿扰关闭 / 全屏应用退出）
                _wasSuppressing = suppressing;
            }
            catch { }
        }
        /// <summary>供桌面页读取历史消息（历史按钮用）。</summary>
        public void ShowIslandHistory() => _islandWindow?.ToggleHistory();

        /// <summary>读取当前班级 id：优先主页“消息提醒班级选择”（本地保存），其次“更多功能”WebView 的 localStorage。</summary>
        private async Task<int?> GetClassIdFromWebViewAsync()
        {
            try
            {
                ClassDataService.Load();
                if (ClassDataService.SelectedClassId > 0) return ClassDataService.SelectedClassId;
            }
            catch { }

            try
            {
                if (WebView.CoreWebView2 != null)
                {
                    string result = await WebView.CoreWebView2.ExecuteScriptAsync(ClassIdReader.Script);
                    return ClassIdReader.Parse(result);
                }
            }
            catch { }
            return null;
        }

        /// <summary>仿 Office 2016 启动画面：加载图标/版本、播放进场动画，定时切到主界面。</summary>
        private void StartupSplash_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
                if (File.Exists(iconPath))
                    SplashIcon.Source = new BitmapImage(new Uri(iconPath));
                SplashVersionText.Text = "V" + CurrentVersion;
            }
            catch { }

            try { SplashEntranceSb.Begin(); } catch { }
            try { SplashFillSb.Begin(); } catch { }

            _splashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SplashVisibleMs) };
            _splashTimer.Tick += (_, _) =>
            {
                _splashTimer?.Stop();
                BeginExitToDesktop();
            };
            _splashTimer.Start();
        }

        /// <summary>启动画面淡出并切换到主界面（含兜底定时，防止动画异常卡在启动画面）。</summary>
        private void BeginExitToDesktop()
        {
            try
            {
                SplashExitSb.Completed += (_, _) => SwitchToDesktop();
                SplashExitSb.Begin();
            }
            catch { SwitchToDesktop(); }

            var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            guard.Tick += (_, _) => { guard.Stop(); SwitchToDesktop(); };
            guard.Start();
        }

        /// <summary>
        /// 加载主页面壁纸：优先使用「设置 → 个性化」里自定义的图片（默认 E:\background.JPG）；
        /// 自定义文件不存在时回退 D:\Smart_Class\background[.png/.jpg/…]，再回退纯色。
        /// </summary>
        public async Task ReloadWallpaperAsync()
        {
            // 优先级：用户自定义 → 程序内置默认壁纸 → 老路径 E:\background.* → D:\Smart_Class\background.* → 纯色
            string custom = ClassDataService.WallpaperPath;
            string? imagePath = File.Exists(custom) ? custom : null;
            if (imagePath == null && !ClassDataService.IsDefaultWallpaper)
                Debug.WriteLine($"[背景] 自定义壁纸不存在：{custom}，改用内置默认壁纸");
            imagePath ??= File.Exists(ClassDataService.BundledWallpaper) ? ClassDataService.BundledWallpaper : null;
            imagePath ??= FindImageFile(@"E:\background");
            imagePath ??= FindImageFile(@"D:\Smart_Class\background");
            Debug.WriteLine($"[背景] 当前壁纸：{imagePath ?? "未找到（纯色兜底）"}");
            IslandLog($"壁纸：{imagePath ?? "未找到（纯色兜底）"}" +
                      (ClassDataService.IsDefaultWallpaper ? "（默认）" : "（自定义）"));

            if (imagePath == null)
            {
                _smartClassBackgroundBrush!.ImageSource = null;
                SmartClassGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.LightGray);
                return;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(imagePath);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                {
                    await bitmap.SetSourceAsync(stream);
                }
                _smartClassBackgroundBrush!.ImageSource = bitmap;
                SmartClassGrid.Background = _smartClassBackgroundBrush;
                Debug.WriteLine("[背景] 图片加载成功");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[背景] 加载失败: {ex.Message}");
                SmartClassGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.LightGray);
            }
        }

        private async Task SetSmartClassBackgroundAsync() => await ReloadWallpaperAsync();

        private string? FindImageFile(string basePath)
        {
            if (File.Exists(basePath)) return basePath;
            string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
            foreach (var ext in extensions)
            {
                string path = basePath + ext;
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private void SwitchToDesktop()
        {
            if (_desktopSwitched) return;
            _desktopSwitched = true;
            try { StartupSplash.Visibility = Visibility.Collapsed; } catch { }
            SmartClassGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Visible;
            PollUsbDrives();               // 立即处理启动画面期间插入的 U 盘
            // 灵动岛按屏幕尺寸重新计算（横幅宽度/历史方形边长），并在主页面显示时常驻纯黑胶囊
            _islandWindow?.SetScreenSize(RootGrid.ActualWidth, RootGrid.ActualHeight);
            _desktopScreenIndex = 0;
            _islandResidentState = false;
            UpdateIslandResident();
            _ = PreloadWebViewAsync();
            _ = ReloadWallpaperAsync();
            _splashTimer?.Stop();
        }

        private async Task PreloadWebViewAsync()
        {
            try
            {
                // 使用 %LOCALAPPDATA% 下的固定用户数据目录（localStorage 持久可用）
                var env = await WebView2EnvironmentHelper.GetAsync();
                await WebView.EnsureCoreWebView2Async(env);
                Debug.WriteLine("[WebView2] 初始化完成（使用系统运行时）");

                // DPI 适配延迟到页面加载完成后
                WebView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess)
                    {
                        try
                        {
                            if (this.Content?.XamlRoot != null)
                            {
                                double dpiScale = this.Content.XamlRoot.RasterizationScale;
                                WebView.RasterizationScale = dpiScale;

                                if (!_dpiBound)
                                {
                                    _dpiBound = true;
                                    this.Content.XamlRoot.Changed += (args, _) =>
                                    {
                                        try
                                        {
                                            if (WebView.CoreWebView2 != null && this.Content?.XamlRoot != null)
                                            {
                                                double newDpi = this.Content.XamlRoot.RasterizationScale;
                                                WebView.RasterizationScale = newDpi;
                                                InjectViewportScript(newDpi);
                                            }
                                        }
                                        catch { }
                                    };
                                }

                                _ = WebView.CoreWebView2.ExecuteScriptAsync(GetViewportScript(dpiScale));
                            }
                        }
                        catch { }

                        DispatcherQueue.TryEnqueue(() => WebView.Focus(FocusState.Programmatic));
                    }
                };

                WebView.CoreWebView2.Navigate("https://f18.llt-service.cn/index-app.html");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebView2] 初始化失败：{ex.Message}");
            }
        }

        private string GetViewportScript(double dpiScale)
        {
            return $@"
                (function() {{
                    var meta = document.querySelector('meta[name=""viewport""]');
                    if (!meta) {{
                        meta = document.createElement('meta');
                        meta.name = 'viewport';
                        document.head.appendChild(meta);
                    }}
                    meta.content = 'width=device-width, initial-scale={dpiScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}, maximum-scale=1.0, user-scalable=no';
                }})();
            ";
        }

        private async void InjectViewportScript(double dpiScale)
        {
            if (WebView.CoreWebView2 == null) return;
            try
            {
                await WebView.CoreWebView2.ExecuteScriptAsync(GetViewportScript(dpiScale));
            }
            catch { }
        }

        public void LoadSmartClassApps()
        {
            _apps.Clear();
            string desktopDir = @"D:\Smart_Class\desktop";
            string iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "金华一中科技校园套件", "Icons");
            Directory.CreateDirectory(iconDir);

            // 枚举桌面目录中的文件与文件夹（仅顶层），均作为应用区图标显示
            var loaded = new List<AppItem>();
            if (Directory.Exists(desktopDir))
            {
                foreach (var entry in Directory.GetFileSystemEntries(desktopDir))
                {
                    try
                    {
                        bool isDir = Directory.Exists(entry);
                        string ext = isDir ? "" : Path.GetExtension(entry).ToLower();
                        string name;
                        string openPath = entry;   // 点击后要打开/启动的目标
                        string iconSource = entry; // 解析图标用的路径

                        if (isDir)
                        {
                            // 文件夹：显示文件夹名，点击在资源管理器中打开；图标走 Shell（支持自定义图标）
                            name = Path.GetFileName(entry);
                        }
                        else if (ext == ".lnk")
                        {
                            // 解析目标；解析失败（如目标不在本机）时以 .lnk 自身作为启动目标，由 Shell 打开
                            string? realTarget = ShellLinkHelper.GetTargetPath(entry);
                            if (!string.IsNullOrEmpty(realTarget))
                            {
                                openPath = realTarget;
                                iconSource = realTarget;
                            }
                            name = Path.GetFileNameWithoutExtension(entry);
                        }
                        else
                        {
                            name = Path.GetFileNameWithoutExtension(entry);
                        }

                        string iconPath = ExtractFileIconToFile(iconSource, iconDir);
                        loaded.Add(new AppItem(name, openPath, iconPath) { SourcePath = entry });
                    }
                    catch { }
                }
            }

            // 手动拖拽排序持久化：先按上次保存的顺序排，新出现的内容补在最后
            var ordered = new List<AppItem>();
            foreach (var source in LoadAppOrder())
            {
                int idx = loaded.FindIndex(x => string.Equals(x.SourcePath, source, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) { ordered.Add(loaded[idx]); loaded.RemoveAt(idx); }
            }
            ordered.AddRange(loaded);
            foreach (var item in ordered) _apps.Add(item);

            // 固定“此电脑”始终置顶（样式与应用一致）
            _apps.Insert(0, new AppItem("此电脑", ThisPcPath, ExtractShellIconToFile(ThisPcPath, iconDir))
            {
                SourcePath = "",
                IsPinned = true
            });
        }

        /// <summary>把当前应用区顺序（不含固定项）保存到本地，下次启动恢复。</summary>
        public void PersistAppOrder()
        {
            try
            {
                var order = _apps
                    .Where(a => !a.IsPinned && !string.IsNullOrEmpty(a.SourcePath))
                    .Select(a => a.SourcePath)
                    .ToList();
                string dir = Path.GetDirectoryName(_appOrderPath) ?? "";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_appOrderPath, JsonSerializer.Serialize(order));
            }
            catch { }
        }

        private List<string> LoadAppOrder()
        {
            try
            {
                if (!File.Exists(_appOrderPath)) return new List<string>();
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_appOrderPath));
                return list ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        /// <summary>通用图标解析：文件夹走 Shell（支持自定义/系统文件夹图标），文件走关联图标。</summary>
        private string ExtractFileIconToFile(string path, string iconDir)
        {
            try
            {
                if (Directory.Exists(path))
                    return ExtractShellIconToFile(path, iconDir);
            }
            catch { }

            try
            {
                string key = "assoc|" + path.ToUpperInvariant();
                string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 8);
                string pngPath = Path.Combine(iconDir, hash + ".png");
                if (File.Exists(pngPath)) return pngPath;

                using Icon? icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    using Bitmap bmp = icon.ToBitmap();
                    bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                    return pngPath;
                }
            }
            catch { }
            return "";
        }

        /// <summary>通过 Shell（SHGetFileInfo）解析路径/CLSID 图标到 png 缓存文件（文件夹、“此电脑”等）。</summary>
        private string ExtractShellIconToFile(string path, string iconDir)
        {
            try
            {
                string key = "shell|" + path.ToUpperInvariant();
                string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 8);
                string pngPath = Path.Combine(iconDir, hash + ".png");
                if (File.Exists(pngPath)) return pngPath;

                IntPtr hIcon = NativeMethods.GetShellIcon(path, large: true);
                if (hIcon == IntPtr.Zero) return "";
                try
                {
                    using (Icon shared = Icon.FromHandle(hIcon))
                    using (Icon icon = (Icon)shared.Clone())
                    using (Bitmap bmp = icon.ToBitmap())
                    {
                        bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    return pngPath;
                }
                finally
                {
                    NativeMethods.DestroyIcon(hIcon);
                }
            }
            catch { return ""; }
        }

        private void UpdateClock(object? sender, object e)
        {
            TaskbarTime.Text = DateTime.Now.ToString("HH:mm");
            TaskbarDate.Text = DateTime.Now.ToString("yyyy/M/d");
            UpdateIslandResident();     // 主页面显示时灵动岛常驻（窗口被最小化/网页层打开时自动隐藏）
            CheckPendingAlerts();       // 勿扰关闭 / 全屏应用退出后补显示未显示过的消息
        }

        /// <summary>由桌面页在切换屏幕时调用：只有第一屏（主页面）显示时灵动岛才常驻。</summary>
        public void OnDesktopScreenChanged(int screenIndex)
        {
            _desktopScreenIndex = screenIndex;
            UpdateIslandResident();
        }

        /// <summary>
        /// 同步“灵动岛是否需要常驻”：**软件进入任何页面（三屏、设置页、更多功能网页层）都常驻显示**，
        /// 只有软件窗口被最小化/隐藏时才收起。收到消息时的置顶展开不受此影响。
        /// </summary>
        private void UpdateIslandResident()
        {
            try
            {
                if (_islandWindow == null) return;

                bool kioskVisible;
                try
                {
                    IntPtr h = WindowNative.GetWindowHandle(this);
                    kioskVisible = NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h);
                }
                catch { kioskVisible = true; }

                if (kioskVisible == _islandResidentState) return;
                _islandResidentState = kioskVisible;
                _islandWindow.SetHomeVisible(kioskVisible);
            }
            catch { }
        }

        private void MoreFeaturesButton_Click(object sender, RoutedEventArgs e)
        {
            SmartClassGrid.Visibility = Visibility.Collapsed;
            WebViewGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Collapsed;
            UpdateIslandResident();

            DispatcherQueue.TryEnqueue(() =>
            {
                WebView.Focus(FocusState.Programmatic);
                DispatcherQueue.TryEnqueue(() => WebView.Focus(FocusState.Programmatic));
            });
        }

        private void BackToDesktopFromWeb_Click(object sender, RoutedEventArgs e)
        {
            WebViewGrid.Visibility = Visibility.Collapsed;
            SmartClassGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Visible;
            ContentFrame.Navigate(typeof(DesktopPage), this);
            UpdateDndVisual();          // 勿扰按钮图标与配置保持一致
        }

        public void NavigateToDesktop()
        {
            WebViewGrid.Visibility = Visibility.Collapsed;
            SmartClassGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Visible;
            ContentFrame.Navigate(typeof(DesktopPage), this);
            UpdateDndVisual();          // 勿扰按钮图标与配置保持一致
        }

        public void NavigateToSettings()
        {
            ContentFrame.Navigate(typeof(SettingsPage), this);
        }

        private readonly Dictionary<long, string> _windowIconCache = new();
        private string _lastTaskbarSig = "";

        private void RefreshTaskbarApps()
        {
            var windows = NativeMethods.GetOpenWindows();

            // 窗口集合未变化时跳过，避免每 2 秒重绘闪烁
            string sig = string.Join("|", windows.Select(w => w.Hwnd + ":" + w.Title));
            if (sig == _lastTaskbarSig) return;
            _lastTaskbarSig = sig;

            TaskbarWindows.Clear();
            foreach (var w in windows)
                TaskbarWindows.Add(new WindowInfoItem
                {
                    Hwnd = w.Hwnd,
                    Title = w.Title,
                    IconPath = GetWindowIconPath(new IntPtr(w.Hwnd))
                });
        }

        private string GetWindowIconPath(IntPtr hwnd)
        {
            long key = hwnd.ToInt64();
            if (_windowIconCache.TryGetValue(key, out var cached)) return cached;

            string iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "金华一中科技校园套件", "WindowIcons");
            Directory.CreateDirectory(iconDir);
            string pngPath = Path.Combine(iconDir, key + ".png");
            try
            {
                IntPtr hIcon = NativeMethods.GetWindowIconHandle(hwnd);
                if (hIcon != IntPtr.Zero)
                {
                    // 句柄属于目标进程，克隆后不得销毁原句柄
                    Icon shared = Icon.FromHandle(hIcon);
                    using (Icon icon = (Icon)shared.Clone())
                    using (Bitmap bmp = icon.ToBitmap())
                    {
                        bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    _windowIconCache[key] = pngPath;
                    return pngPath;
                }
            }
            catch { }
            _windowIconCache[key] = "";
            return "";
        }

        private void TaskbarApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is long hwndLong)
            {
                NativeMethods.ActivateWindow(new IntPtr(hwndLong));
            }
        }

        // ==================== 系统托盘功能 ====================

        private void ClockButton_Click(object sender, RoutedEventArgs e) => OpenUrl("ms-settings:datetime");

        private void NetworkButton_Click(object sender, RoutedEventArgs e) => OpenUrl("ms-settings:network");

        private void BatteryButton_Click(object sender, RoutedEventArgs e) => OpenUrl("ms-settings:powersleep");

        // ==================== 勿扰模式（侧边栏开关） ====================

        private void DndButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool next = !ClassDataService.DndEnabled;
                ClassDataService.SetDnd(next);
                IslandLog(next ? "勿扰模式：开启（消息不提醒，打铃照常）" : "勿扰模式：关闭（补显示未显示过的消息）");
                UpdateDndVisual();
            }
            catch { }
        }

        /// <summary>刷新勿扰按钮的图标颜色（开启时用强调色 + 提示文字）。</summary>
        private void UpdateDndVisual()
        {
            try
            {
                bool on = ClassDataService.DndEnabled;
                if (DndIcon != null)
                {
                    DndIcon.Foreground = new SolidColorBrush(on
                        ? Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x3A, 0x8A)      // 开启：深蓝
                        : Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
                    DndIcon.Opacity = on ? 1.0 : 0.75;
                }
                if (DndButton != null)
                    DndButton.Background = on
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x1E, 0x3A, 0x8A))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                ToolTipService.SetToolTip(DndButton, on
                    ? "勿扰模式已开启（不提醒消息，打铃照常）—— 点击关闭"
                    : "勿扰模式（不提醒消息，打铃照常）");
            }
            catch { }
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("Sndvol.exe") { UseShellExecute = true });
            }
            catch { }
        }

        private void ShowDesktopButton_Click(object sender, RoutedEventArgs e) => ShowDesktop();

        /// <summary>直接显示真实桌面：最小化任务栏中的其他窗口 + 本程序自身。</summary>
        public void ShowDesktop()
        {
            foreach (var w in NativeMethods.GetOpenWindows())
            {
                NativeMethods.ShowWindow(new IntPtr(w.Hwnd), NativeMethods.SW_MINIMIZE);
            }
            MinimizeWindow();
        }

        private void MinimizeWindow()
        {
            try
            {
                NativeMethods.ShowWindow(WindowNative.GetWindowHandle(this), NativeMethods.SW_MINIMIZE);
            }
            catch { }
        }

        private double _lastPointerY;
        private void OnRootGridPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SmartClassGrid.Visibility != Visibility.Visible) return;
            var point = e.GetCurrentPoint(RootGrid).Position;
            _lastPointerY = point.Y;
            if (RootGrid.ActualHeight - point.Y < 10)
            {
                ShowTaskbar();
                _hideTaskbarTimer?.Start();
            }
            else _hideTaskbarTimer?.Stop();
        }

        private void HideTaskbarTimer_Tick(object? sender, object e)
        {
            _hideTaskbarTimer?.Stop();
            if (_lastPointerY < RootGrid.ActualHeight - 10) HideTaskbar();
        }

        private void ShowTaskbar()
        {
            if (TaskbarTransform == null) return;
            var anim = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(anim, TaskbarTransform);
            Storyboard.SetTargetProperty(anim, "Y");
            var sb = new Storyboard(); sb.Children.Add(anim); sb.Begin();
        }

        private void HideTaskbar()
        {
            if (TaskbarTransform == null || TaskbarBorder == null) return;
            var anim = new DoubleAnimation { To = TaskbarBorder.Height, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(anim, TaskbarTransform);
            Storyboard.SetTargetProperty(anim, "Y");
            var sb = new Storyboard(); sb.Children.Add(anim); sb.Begin();
        }

        private void LoadPreviewSetting()
        {
            try
            {
                if (File.Exists(_previewDataPath))
                {
                    var json = File.ReadAllText(_previewDataPath);
                    if (!string.IsNullOrEmpty(json))
                        _isPreviewEnabled = JsonSerializer.Deserialize<bool>(json);
                }
            }
            catch { }
        }

        public void SavePreviewSetting(bool enabled)
        {
            _isPreviewEnabled = enabled;
            try
            {
                string dir = Path.GetDirectoryName(_previewDataPath) ?? "";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_previewDataPath, JsonSerializer.Serialize(enabled));
            }
            catch { }
        }

        public bool IsPreviewEnabled => _isPreviewEnabled;
        public void SetHeaderBackground(SolidColorBrush brush) { }
        public void SetTaskbarHeight(double height) { if (TaskbarBorder != null) TaskbarBorder.Height = height; }

        private void StartButton_Click(object sender, RoutedEventArgs e) { }
        private async void PowerButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ContentDialog
            {
                Title = "电源",
                Content = "确定要退出程序吗？",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                Application.Current.Exit();
        }
        private void StartSettings_Click(object sender, RoutedEventArgs e) { NavigateToSettings(); }
        private void ExplorerButton_Click(object sender, RoutedEventArgs e) { OpenRealExplorer(null); }
        private void ExitButton_Click(object sender, RoutedEventArgs e) => MinimizeWindow();

        /// <summary>执行一次更新检查。</summary>
        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            return await UpdateChecker.CheckAsync(_httpClient, CurrentVersion, UpdateUrl, DownloadUrl);
        }

        private async Task CheckForUpdatesAtStartupAsync()
        {
            try
            {
                var info = await CheckForUpdatesAsync();
                if (info.IsUpdateAvailable)
                    await ShowUpdateDialogAsync(info);
            }
            catch
            {
                // 启动时静默检查失败不打扰用户
            }
        }

        private async Task ShowUpdateDialogAsync(UpdateInfo info)
        {
            string content = $"检测到新版本 {info.LatestVersion}（当前版本 {info.CurrentVersion}）。";
            if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
                content += Environment.NewLine + Environment.NewLine + info.ReleaseNotes;

            var dlg = new ContentDialog
            {
                Title = "发现新版本",
                Content = content,
                PrimaryButtonText = "立即更新",
                CloseButtonText = "稍后",
                XamlRoot = this.Content.XamlRoot
            };

            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                OpenUrl(info.DownloadUrl);
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }

    public class WindowInfoItem
    {
        public long Hwnd { get; set; }
        public string Title { get; set; } = "";
        public string IconPath { get; set; } = "";

        public ImageSource? Icon
        {
            get
            {
                if (string.IsNullOrEmpty(IconPath) || !File.Exists(IconPath))
                    return null;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new Uri(IconPath);
                    return bitmap;
                }
                catch { return null; }
            }
        }
    }
}
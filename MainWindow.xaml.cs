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
using Windows.Media.Playback;
using Windows.Storage;

namespace SmartClassNight
{
    public sealed partial class MainWindow : Window
    {
        private const string CurrentVersion = "1.1.3";
        private const string UpdateUrl = "https://f18.llt-service.cn/update.txt";
        private const string DownloadUrl = "https://f18.llt-service.cn/download";
        private readonly HttpClient _httpClient = new();
        private DispatcherTimer? _clockTimer;
        private DispatcherTimer? _taskbarRefreshTimer;
        private DispatcherTimer? _hideTaskbarTimer;
        private DispatcherTimer? _videoFallbackTimer;

        private ObservableCollection<AppItem> _apps = new();
        public ObservableCollection<AppItem> Apps => _apps;

        private bool _isPreviewEnabled = false;
        private string _previewDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "金华一中科技校园套件", "preview.json");

        public ObservableCollection<WindowInfoItem> TaskbarWindows { get; } = new();

        private ImageBrush? _smartClassBackgroundBrush;
        private bool _dpiBound = false;
        private bool _videoStarted = false;

        public MainWindow()
        {
            this.InitializeComponent();

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            RootGrid.PointerMoved += OnRootGridPointerMoved;

            LoadPreviewSetting();

            _smartClassBackgroundBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
            SmartClassGrid.Background = _smartClassBackgroundBrush;

            _videoFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _videoFallbackTimer.Tick += (s, e) =>
            {
                _videoFallbackTimer.Stop();
                if (!_videoStarted)
                    SwitchToDesktop();
            };
            _videoFallbackTimer.Start();

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

            StartupVideo.Loaded += StartupVideo_Loaded;
        }

        private void StartupVideo_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var mediaPlayer = StartupVideo.MediaPlayer;

                mediaPlayer.MediaOpened += (s, args) =>
                {
                    _videoStarted = true;
                    PluginRunner.RunStartupPlugins();
                    Debug.WriteLine("[视频] 媒体已打开，开始播放");
                    mediaPlayer.Play();
                };

                mediaPlayer.MediaEnded += (s, args) =>
                {
                    Debug.WriteLine("[视频] 播放完毕");
                    DispatcherQueue.TryEnqueue(() => SwitchToDesktop());
                };

                mediaPlayer.MediaFailed += (s, args) =>
                {
                    Debug.WriteLine("[视频] 播放失败：" + args.ErrorMessage);
                    DispatcherQueue.TryEnqueue(() => SwitchToDesktop());
                };

                if (mediaPlayer.PlaybackSession != null)
                    mediaPlayer.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[视频] 初始化异常：" + ex.Message);
                DispatcherQueue.TryEnqueue(() => SwitchToDesktop());
            }
        }

        private async Task SetSmartClassBackgroundAsync()
        {
            string basePath = @"D:\Smart_Class\background";
            string? imagePath = FindImageFile(basePath);
            Debug.WriteLine($"[背景] 查找文件 {basePath}，结果: {imagePath ?? "未找到"}");

            if (imagePath == null)
            {
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
                Debug.WriteLine("[背景] 图片加载成功");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[背景] 加载失败: {ex.Message}");
                SmartClassGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.LightGray);
            }
        }

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
            try
            {
                StartupVideo.MediaPlayer.Pause();
                StartupVideo.MediaPlayer.Source = null;
            }
            catch { }
            StartupVideo.Visibility = Visibility.Collapsed;
            SmartClassGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Visible;
            LoadSmartClassApps();
            _ = PreloadWebViewAsync();
            _ = SetSmartClassBackgroundAsync();
            _videoFallbackTimer?.Stop();
            _videoStarted = true;
        }

        private async Task PreloadWebViewAsync()
        {
            try
            {
                await WebView.EnsureCoreWebView2Async();
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
            if (!Directory.Exists(desktopDir))
                return;

            string iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "金华一中科技校园套件", "Icons");
            Directory.CreateDirectory(iconDir);

            foreach (var file in Directory.GetFiles(desktopDir))
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file).ToLower();
                    string target = file;
                    string iconPath = "";

                    if (ext == ".lnk")
                    {
                        string? realTarget = ShellLinkHelper.GetTargetPath(file);
                        if (!string.IsNullOrEmpty(realTarget))
                        {
                            target = realTarget;
                            name = Path.GetFileNameWithoutExtension(realTarget);
                            iconPath = ExtractIconToFile(target, iconDir);
                        }
                        else continue;
                    }
                    else
                    {
                        iconPath = ExtractIconToFile(file, iconDir);
                    }
                    _apps.Add(new AppItem(name, target, iconPath));
                }
                catch { }
            }
        }

        private string ExtractIconToFile(string filePath, string iconDir)
        {
            try
            {
                string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(filePath))).Substring(0, 8);
                string pngPath = Path.Combine(iconDir, $"{hash}.png");
                if (File.Exists(pngPath)) return pngPath;

                using Icon icon = Icon.ExtractAssociatedIcon(filePath);
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

        private void UpdateClock(object? sender, object e)
        {
            TaskbarTime.Text = DateTime.Now.ToString("HH:mm");
        }

        private void MoreFeaturesButton_Click(object sender, RoutedEventArgs e)
        {
            SmartClassGrid.Visibility = Visibility.Collapsed;
            WebViewGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Collapsed;

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
        }

        public void NavigateToDesktop()
        {
            WebViewGrid.Visibility = Visibility.Collapsed;
            SmartClassGrid.Visibility = Visibility.Visible;
            MoreFeaturesButton.Visibility = Visibility.Visible;
            ContentFrame.Navigate(typeof(DesktopPage), this);
        }

        public void NavigateToSettings()
        {
            ContentFrame.Navigate(typeof(SettingsPage), this);
        }

        private void RefreshTaskbarApps()
        {
            var windows = NativeMethods.GetOpenWindows();
            TaskbarWindows.Clear();
            foreach (var w in windows)
                TaskbarWindows.Add(new WindowInfoItem { Hwnd = w.Hwnd, Title = w.Title });
        }

        private void TaskbarApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is long hwndLong)
            {
                IntPtr hwnd = new IntPtr(hwndLong);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
            }
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
        private void ExplorerButton_Click(object sender, RoutedEventArgs e) { Process.Start("explorer.exe"); }
        private void ExitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
    }

    public class WindowInfoItem
    {
        public long Hwnd { get; set; }
        public string Title { get; set; } = "";
    }
}
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SmartClassNight
{
    public sealed partial class SettingsPage : Page
    {
        internal static MainWindow? ParentWindowStatic;
        private bool _isLoaded = false;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;
            _isLoaded = true;

            NavListView.SelectedIndex = 0;
            SettingsContentControl.Content = new PersonalizationPage();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            ParentWindowStatic = e.Parameter as MainWindow;
        }

        private void BackToHome_Click(object sender, RoutedEventArgs e)
        {
            if (ParentWindowStatic != null)
                ParentWindowStatic.NavigateToDesktop();
        }

        private async void NavListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded) return;
            if (NavListView.SelectedItem is ListViewItem item)
            {
                string? tag = item.Tag?.ToString();
                switch (tag)
                {
                    case "Personalization": SettingsContentControl.Content = new PersonalizationPage(); break;
                    case "Apps": SettingsContentControl.Content = new AppManagePage(); break;
                    case "Taskbar": SettingsContentControl.Content = new TaskbarSettingsPage(); break;
                    case "Preview": SettingsContentControl.Content = new PreviewPage(); break;
                    case "Update": SettingsContentControl.Content = new UpdatePage(); break;
                    case "About": SettingsContentControl.Content = new AboutPage(); break;
                    case "ClassIsland":
                        // 打开 ClassIsland 的设置页面（默认基本设置）
                        await OpenClassIslandSettings("general");
                        NavListView.SelectedItem = null;
                        break;
                    case "ClassIslandProfile":
                        // 打开 ClassIsland 的档案编辑
                        await OpenClassIslandProfile();
                        NavListView.SelectedItem = null;
                        break;
                }
            }
        }

        // 打开 ClassIsland 设置（settings 子页面）
        private async System.Threading.Tasks.Task OpenClassIslandSettings(string pageId)
        {
            var uri = new Uri($"classisland://app/settings/{pageId}");
            var success = await Launcher.LaunchUriAsync(uri);
            if (!success)
            {
                await ShowClassIslandNotAvailableDialog();
            }
        }

        // 打开 ClassIsland 档案编辑
        private async System.Threading.Tasks.Task OpenClassIslandProfile()
        {
            var uri = new Uri("classisland://app/profile/");
            var success = await Launcher.LaunchUriAsync(uri);
            if (!success)
            {
                await ShowClassIslandNotAvailableDialog();
            }
        }

        // 公共错误提示对话框
        private async System.Threading.Tasks.Task ShowClassIslandNotAvailableDialog()
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "提示",
                Content = "无法打开 ClassIsland 功能。请确保 ClassIsland 已安装，并在其「基本」设置中启用了「注册 Url 协议」选项。",
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    // ==================== 个性化 ====================
    public sealed class PersonalizationPage : Page
    {
        public PersonalizationPage()
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            var title = new TextBlock { Text = "个性化", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue) };
            var themeLabel = new TextBlock { Text = "标题栏颜色（已失效）", Margin = new Thickness(0, 12, 0, 4) };
            var combo = new ComboBox { Width = 200 };
            combo.Items.Add(new ComboBoxItem { Content = "深蓝", Tag = "#FF1E3A8A" });
            combo.Items.Add(new ComboBoxItem { Content = "墨绿", Tag = "#FF2E5D3A" });
            combo.Items.Add(new ComboBoxItem { Content = "暗紫", Tag = "#FF5E2E7D" });
            combo.Items.Add(new ComboBoxItem { Content = "橙红", Tag = "#FFB84D32" });
            combo.SelectedIndex = 0;
            combo.SelectionChanged += (s, e) =>
            {
                var main = SettingsPage.ParentWindowStatic;
                if (main != null && combo.SelectedItem is ComboBoxItem item && item.Tag is string colorStr)
                {
                    byte r = Convert.ToByte(colorStr.Substring(3, 2), 16);
                    byte g = Convert.ToByte(colorStr.Substring(5, 2), 16);
                    byte b = Convert.ToByte(colorStr.Substring(7, 2), 16);
                    main.SetHeaderBackground(new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)));
                }
            };
            stack.Children.Add(title);
            stack.Children.Add(themeLabel);
            stack.Children.Add(combo);

            // 主页面壁纸（默认 E:\background.JPG，可自选图片文件）
            var wallpaperTitle = new TextBlock
            {
                Text = "主页面壁纸",
                FontSize = 16,
                Margin = new Thickness(0, 20, 0, 4)
            };
            var wallpaperText = new TextBlock
            {
                Text = "当前：" + ClassDataService.WallpaperPath,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var wallpaperRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var pickWallpaperBtn = new Button
            {
                Content = "选择图片…",
                Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            pickWallpaperBtn.Click += async (s, e) =>
            {
                try
                {
                    var main = SettingsPage.ParentWindowStatic ?? AppHost.Main;
                    IntPtr owner = IntPtr.Zero;
                    try { if (main != null) owner = WindowNative.GetWindowHandle(main); } catch { }

                    var picked = FileDialogs.PickFiles(owner, "选择主页面壁纸", FileDialogs.ImageFilter, multiSelect: false);
                    if (picked.Count == 0) return;

                    ClassDataService.SetWallpaper(picked[0]);
                    wallpaperText.Text = "当前：" + ClassDataService.WallpaperPath;
                    if (main != null) await main.ReloadWallpaperAsync();
                }
                catch { }
            };
            var resetWallpaperBtn = new Button { Content = "恢复默认（E:\\background.JPG）" };
            resetWallpaperBtn.Click += async (s, e) =>
            {
                try
                {
                    ClassDataService.SetWallpaper("");
                    wallpaperText.Text = "当前：" + ClassDataService.WallpaperPath + "（默认）";
                    var main = SettingsPage.ParentWindowStatic ?? AppHost.Main;
                    if (main != null) await main.ReloadWallpaperAsync();
                }
                catch { }
            };
            wallpaperRow.Children.Add(pickWallpaperBtn);
            wallpaperRow.Children.Add(resetWallpaperBtn);

            stack.Children.Add(wallpaperTitle);
            stack.Children.Add(wallpaperText);
            stack.Children.Add(wallpaperRow);

            // 消息提醒开关（勿扰模式 / 主页面隐藏）
            var notifyTitle = new TextBlock
            {
                Text = "消息提醒",
                FontSize = 16,
                Margin = new Thickness(0, 20, 0, 4)
            };
            var dndToggle = new ToggleSwitch
            {
                Header = "勿扰模式（不提醒消息；打铃系统照常运行）",
                OnContent = "开",
                OffContent = "关",
                IsOn = ClassDataService.DndEnabled
            };
            dndToggle.Toggled += (s, e) => ClassDataService.SetDnd(dndToggle.IsOn);

            var homeHideToggle = new ToggleSwitch
            {
                Header = "停留在主页面时隐藏灵动岛消息提醒（右上角“最近消息”仍会显示）",
                OnContent = "开",
                OffContent = "关",
                IsOn = ClassDataService.HideAlertOnHome,
                Margin = new Thickness(0, 8, 0, 0)
            };
            homeHideToggle.Toggled += (s, e) => ClassDataService.SetHideAlertOnHome(homeHideToggle.IsOn);

            var notifyDesc = new TextBlock
            {
                Text = "另有两条自动抑制规则：① 前台是别的程序的全屏窗口时不弹消息提醒；" +
                       "② 停留主页面且上面开关打开时只更新“最近消息”卡片。被勿扰/全屏抑制的消息会在条件解除后自动补显示。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(notifyTitle);
            stack.Children.Add(dndToggle);
            stack.Children.Add(homeHideToggle);
            stack.Children.Add(notifyDesc);
            // 新消息提示音（声音与网页 f18.llt-service.cn/weather 的通知逻辑一致）
            var soundTitle = new TextBlock
            {
                Text = "消息提示音",
                FontSize = 16,
                Margin = new Thickness(0, 20, 0, 4)
            };
            var soundToggle = new ToggleSwitch
            {
                Header = "收到新消息时播放提示音",
                OnContent = "开",
                OffContent = "关",
                IsOn = ClassDataService.NotifySoundEnabled
            };
            soundToggle.Toggled += (s, e) => ClassDataService.SetNotifySound(soundToggle.IsOn);

            var soundDesc = new TextBlock
            {
                Text = "提示音与网页版“智慧课堂系统功能区”的通知铃声相同；声音文件随程序一起安装，断网也能响。" +
                       "若某次无法播放，会自动退化为 880Hz 短提示音。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0)
            };
            var soundTestBtn = new Button
            {
                Content = "试听提示音",
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            soundTestBtn.Click += (s, e) => NotificationSound.Preview();   // 试听不受开关限制

            stack.Children.Add(soundTitle);
            stack.Children.Add(soundToggle);
            stack.Children.Add(soundDesc);
            stack.Children.Add(soundTestBtn);

            this.Content = stack;
        }
    }

    // ==================== 应用管理 ====================
    public sealed class AppManagePage : Page
    {
        private ListView? _listView;

        public AppManagePage()
        {
            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            var headPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            headPanel.Children.Add(new TextBlock { Text = "应用管理", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue) });
            headPanel.Children.Add(new TextBlock
            {
                Text = "选择 .exe 或 .lnk，自动在 D:\\Smart_Class\\desktop 创建快捷方式；移除仅删除快捷方式，不影响原程序。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            Grid.SetRow(headPanel, 0);
            grid.Children.Add(headPanel);

            _listView = new ListView { Margin = new Thickness(0, 8, 0, 8) };
            _listView.DisplayMemberPath = "Name";
            _listView.SelectionMode = ListViewSelectionMode.Single;
            Grid.SetRow(_listView, 1);
            grid.Children.Add(_listView);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var addBtn = new Button
            {
                Content = "添加应用…",
                Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            addBtn.Click += async (s, e) => await AddApplicationAsync();
            var removeBtn = new Button { Content = "移除选中" };
            removeBtn.Click += async (s, e) => await RemoveSelectedAsync();
            var refreshBtn = new Button { Content = "刷新列表" };
            refreshBtn.Click += (s, e) => RefreshList();
            btnRow.Children.Add(addBtn);
            btnRow.Children.Add(removeBtn);
            btnRow.Children.Add(refreshBtn);
            Grid.SetRow(btnRow, 2);
            grid.Children.Add(btnRow);

            this.Content = grid;
            this.Loaded += (s, e) => RefreshList();
        }

        private void RefreshList()
        {
            var m = SettingsPage.ParentWindowStatic;
            if (m == null) return;
            m.LoadSmartClassApps();
            _listView!.ItemsSource = null;
            // 固定项（此电脑）不参与应用管理列表
            _listView.ItemsSource = m.Apps.Where(a => !a.IsPinned).ToList();
        }

        private async System.Threading.Tasks.Task AddApplicationAsync()
        {
            var m = SettingsPage.ParentWindowStatic;
            if (m == null) return;

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(m));

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                if (!File.Exists(file.Path)) return;
                string desktopDir = @"D:\Smart_Class\desktop";
                Directory.CreateDirectory(desktopDir);
                string name = Path.GetFileNameWithoutExtension(file.Path);
                string lnkPath = Path.Combine(desktopDir, name + ".lnk");
                if (file.FileType.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file.Path, lnkPath, true);
                else
                    ShellLinkHelper.CreateShortcut(lnkPath, file.Path);
            }
            catch { }
            RefreshList();
        }

        private async System.Threading.Tasks.Task RemoveSelectedAsync()
        {
            var m = SettingsPage.ParentWindowStatic;
            if (m == null || _listView!.SelectedItem is not AppItem item) return;
            if (string.IsNullOrEmpty(item.SourcePath)) return;

            var dlg = new ContentDialog
            {
                Title = "移除确认",
                Content = $"确定要将“{item.Name}”从应用区移除吗？（不会删除原程序）",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                if (File.Exists(item.SourcePath))
                    File.Delete(item.SourcePath);
            }
            catch { }
            RefreshList();
        }
    }

    // ==================== 任务栏设置 ====================
    public sealed class TaskbarSettingsPage : Page
    {
        public TaskbarSettingsPage()
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            var title = new TextBlock { Text = "任务栏设置", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue) };
            var heightLabel = new TextBlock { Text = "高度", Margin = new Thickness(0, 12, 0, 4) };
            var heightSlider = new Slider { Minimum = 36, Maximum = 64, Value = 48, TickFrequency = 4, Width = 200 };
            heightSlider.ValueChanged += (s, e) =>
            {
                var m = SettingsPage.ParentWindowStatic;
                if (m != null) m.SetTaskbarHeight(e.NewValue);
            };
            stack.Children.Add(title);
            stack.Children.Add(heightLabel);
            stack.Children.Add(heightSlider);
            this.Content = stack;
        }
    }

    // ==================== 预览体验计划 ====================
    public sealed class PreviewPage : Page
    {
        public PreviewPage()
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            var title = new TextBlock { Text = "预览体验计划", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue) };
            var toggle = new ToggleSwitch { Header = "加入预览体验计划" };
            toggle.Toggled += (s, e) =>
            {
                var main = SettingsPage.ParentWindowStatic;
                if (main != null) main.SavePreviewSetting(toggle.IsOn);
            };

            // 灵动岛预览：不依赖服务端消息，直接演示“展开 → 收到新消息 2 秒 → 从右向左滚动两遍 → 收起”
            var islandTitle = new TextBlock
            {
                Text = "灵动岛",
                FontSize = 16,
                Margin = new Thickness(0, 18, 0, 4)
            };
            var islandDesc = new TextBlock
            {
                Text = "点击下方按钮可预览灵动岛效果（不影响真实消息推送）。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap
            };
            var islandTestBtn = new Button
            {
                Content = "预览灵动岛",
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            islandTestBtn.Click += (s, e) => SettingsPage.ParentWindowStatic?.PreviewDynamicIsland();

            stack.Children.Add(title);
            stack.Children.Add(toggle);
            stack.Children.Add(islandTitle);
            stack.Children.Add(islandDesc);
            stack.Children.Add(islandTestBtn);
            this.Content = stack;

            this.Loaded += (s, e) =>
            {
                var main = SettingsPage.ParentWindowStatic;
                if (main != null) toggle.IsOn = main.IsPreviewEnabled;
            };
        }
    }

    // ==================== 更新与反馈 ====================
    public sealed class UpdatePage : Page
    {
        private TextBlock _statusText = null!;
        private Button _checkButton = null!;
        private Button _downloadButton = null!;
        private bool _checking = false;

        public UpdatePage()
        {
            var stack = new StackPanel { Margin = new Thickness(20) };

            var title = new TextBlock { Text = "更新与反馈", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue) };

            var versionText = new TextBlock
            {
                Text = "当前版本：" + MainWindow.CurrentVersion,
                Margin = new Thickness(0, 12, 0, 4)
            };

            _checkButton = new Button
            {
                Content = "检查更新",
                Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _checkButton.Click += async (s, e) => await CheckUpdatesAsync();

            _statusText = new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            _downloadButton = new Button
            {
                Content = "下载新版本",
                Background = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _downloadButton.Click += (s, e) =>
            {
                if (_downloadButton.Tag is string url)
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { }
                }
            };

            var feedbackButton = new Button
            {
                Content = "反馈问题",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            feedbackButton.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(MainWindow.FeedbackUrl) { UseShellExecute = true }); }
                catch { }
            };

            stack.Children.Add(title);
            stack.Children.Add(versionText);
            stack.Children.Add(_checkButton);
            stack.Children.Add(_statusText);
            stack.Children.Add(_downloadButton);
            stack.Children.Add(feedbackButton);
            this.Content = stack;
        }

        private async System.Threading.Tasks.Task CheckUpdatesAsync()
        {
            if (_checking) return;
            var main = SettingsPage.ParentWindowStatic;
            if (main == null)
            {
                _statusText.Text = "无法获取主窗口，请返回主页后重试。";
                return;
            }

            _checking = true;
            _checkButton.IsEnabled = false;
            _downloadButton.Visibility = Visibility.Collapsed;
            _statusText.Text = "正在检查更新…";

            try
            {
                var info = await main.CheckForUpdatesAsync();
                if (!info.Succeeded)
                {
                    _statusText.Text = info.ErrorMessage;
                }
                else if (info.IsUpdateAvailable)
                {
                    string notes = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                        ? ""
                        : Environment.NewLine + Environment.NewLine + info.ReleaseNotes;
                    _statusText.Text = $"发现新版本 {info.LatestVersion}（当前版本 {info.CurrentVersion}）{notes}";
                    _downloadButton.Tag = info.DownloadUrl;
                    _downloadButton.Visibility = Visibility.Visible;
                }
                else
                {
                    _statusText.Text = "当前已是最新版本。";
                }
            }
            catch (Exception ex)
            {
                _statusText.Text = "检查更新失败：" + ex.Message;
            }
            finally
            {
                _checking = false;
                _checkButton.IsEnabled = true;
            }
        }
    }

    // ==================== 关于 ====================
    public sealed class AboutPage : Page
    {
        public AboutPage()
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "智慧课堂系统", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            stack.Children.Add(new TextBlock { Text = "版本 " + MainWindow.CurrentVersion, Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "由支振超独立运营", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "本软件由C#制作，如在使用期间遇到问题，欢迎前往“更新与反馈”页面反馈！", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "可在“更新与反馈”页面检查软件更新。", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "应用区添加应用方法：前往“设置 → 应用管理”点击“添加应用”选择程序即可；也可以将快捷方式复制到D:/Smart_Class/desktop目录下（安装时已自动创建该目录）", Margin = new Thickness(0, 10, 0, 0) });
            this.Content = stack;
        }
    }
}
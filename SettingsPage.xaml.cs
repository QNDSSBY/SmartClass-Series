using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.System;

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
            this.Content = stack;
        }
    }

    // ==================== 应用管理（只读刷新） ====================
    public sealed class AppManagePage : Page
    {
        private ListView? _listView;

        public AppManagePage()
        {
            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            var header = new TextBlock { Text = "应用管理", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue) };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            _listView = new ListView { Margin = new Thickness(0, 8, 0, 8) };
            _listView.DisplayMemberPath = "Name";
            Grid.SetRow(_listView, 1);
            grid.Children.Add(_listView);

            var refreshBtn = new Button { Content = "刷新列表", Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
            refreshBtn.Click += (s, e) =>
            {
                var m = SettingsPage.ParentWindowStatic;
                if (m != null)
                {
                    m.LoadSmartClassApps();
                    _listView.ItemsSource = null;
                    _listView.ItemsSource = m.Apps;
                }
            };
            Grid.SetRow(refreshBtn, 2);
            grid.Children.Add(refreshBtn);

            this.Content = grid;
            this.Loaded += (s, e) =>
            {
                var m = SettingsPage.ParentWindowStatic;
                if (m != null) _listView!.ItemsSource = m.Apps;
            };
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
            stack.Children.Add(title);
            stack.Children.Add(toggle);
            this.Content = stack;

            this.Loaded += (s, e) =>
            {
                var main = SettingsPage.ParentWindowStatic;
                if (main != null) toggle.IsOn = main.IsPreviewEnabled;
            };
        }
    }

    // ==================== 关于 ====================
    public sealed class AboutPage : Page
    {
        public AboutPage()
        {
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "智慧课堂系统", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            stack.Children.Add(new TextBlock { Text = "版本 1.1.4", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "由支振超独立运营", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "本软件由C#制作，如在使用期间遇到问题，欢迎前往“更新与反馈”页面反馈！", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "由于软件仍有缺陷，更新检查可能仍有问题，请定期前往“更新与反馈”页面检查更新！", Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(new TextBlock { Text = "应用区添加应用方法：将应用快捷方式复制到D:/Smart_Class/desktop目录下（如没有请手动创建）", Margin = new Thickness(0, 10, 0, 0) });
            this.Content = stack;
        }
    }
}
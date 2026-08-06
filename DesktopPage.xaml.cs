using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SmartClassNight
{
    public sealed partial class DesktopPage : Page
    {
        private DispatcherTimer? _desktopClockTimer;
        private CancellationTokenSource? _cts;

        public MainWindow ParentWindow { get; set; } = null!;

        private bool _isPageReady = false;

        public DesktopPage()
        {
            this.InitializeComponent();
            this.Loaded += DesktopPage_Loaded;
            this.Unloaded += DesktopPage_Unloaded;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            ParentWindow = (e.Parameter as MainWindow)!;
            if (ParentWindow != null)
            {
                if (ParentWindow.Apps.Count == 0)
                    ParentWindow.LoadSmartClassApps();
                AppsGrid.ItemsSource = ParentWindow.Apps;
            }
        }

        private void DesktopPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isPageReady) return;
            _isPageReady = true;

            _cts = new CancellationTokenSource();
            _desktopClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _desktopClockTimer.Tick += UpdateDesktopClock;
            _desktopClockTimer.Start();
            UpdateDesktopClock(null, null!);

            _ = LoadWeatherAsync();
        }

        private void DesktopPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _desktopClockTimer?.Stop();
            _isPageReady = false;
        }

        private void UpdateDesktopClock(object? sender, object e)
        {
            if (!_isPageReady) return;
            var now = DateTime.Now;
            LockTimeText.Text = now.ToString("HH:mm");
            FullDateText.Text = $"{now.Month}月{now.Day}日";
            DayOfWeekText.Text = now.DayOfWeek.ToString();
        }

        private async Task LoadWeatherAsync()
        {
            try
            {
                await WeatherWebView.EnsureCoreWebView2Async();
                WeatherWebView.CoreWebView2.Navigate("https://f18.llt-service.cn/weather");
            }
            catch { }
        }

        private async void AppItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    var dlg = new ContentDialog
                    {
                        Title = "错误",
                        Content = $"无法启动：{ex.Message}",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await dlg.ShowAsync();
                }
            }
        }
    }
}
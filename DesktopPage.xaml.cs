using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace SmartClassNight
{
    /// <summary>
    /// 主页面（三屏横向分页，左右滑动切换）：
    /// - 第一屏：时钟 + **今日课表**（左，样式参考值日）+ 天气 / 未来2小时降水 / 今日值日 / 消息提醒班级 / **最近一条消息（大字）**（右）；
    /// - 第二屏：校园自动打铃系统（BellPage）；
    /// - 第三屏：点歌系统（MusicPage）。
    /// 应用区（桌面快捷方式图标墙）已按要求移除。
    /// </summary>
    public sealed partial class DesktopPage : Page
    {
        private DispatcherTimer? _desktopClockTimer;
        private DispatcherTimer? _weatherTimer;
        private DispatcherTimer? _precipTimer;
        private DispatcherTimer? _dutyTimer;
        private DispatcherTimer? _scheduleTimer;
        private DispatcherTimer? _homeworkRefreshTimer;

        // 作业轮播状态
        private List<HomeworkItem> _homework = new();
        private int _homeworkIndex;
        private DispatcherTimer? _homeworkCarouselTimer;

        private bool _isPageReady;
        private bool _classComboReady;
        private double _screenWidth = 1;

        private const int ScreenCount = 3;
        /// <summary>横向滑动的触发阈值（DIP）。</summary>
        private const double SwipeThreshold = 50;
        private int _currentScreen;
        /// <summary>目标屏幕（分页被内部控件“焦点自动滚动”带偏时用它纠偏）。</summary>
        private int _targetScreen;
        private DateTime _lastPagerPointer = DateTime.MinValue;
        private DateTime _lastPagerPointerUp = DateTime.MinValue;
        private bool _pagerPointerDown;

        // 滑动识别
        private bool _swipeTracking;
        private Windows.Foundation.Point _swipeStart;
        private uint _swipePointerId;
        private DateTime _lastSwipeAt = DateTime.MinValue;
        private DateTime _lastUserPanAt = DateTime.MinValue;   // 用户拖动分页（中间态）的时间

        public MainWindow ParentWindow { get; set; } = null!;

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
                // 首次进入时直接与灵动岛状态对齐（不播放动画）
                ClockPanel.Opacity = ParentWindow.IsIslandExpanded ? 0 : 1;
            }
        }

        private void DesktopPage_Loaded(object sender, RoutedEventArgs e)
        {
            BuildScreenDots();

            if (_isPageReady) return;
            _isPageReady = true;

            _desktopClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _desktopClockTimer.Tick += UpdateDesktopClock;
            _desktopClockTimer.Start();
            UpdateDesktopClock(null, null!);

            // 时间文字加白色描边（两层：近处实白 + 远处淡白，形成柔和描边/外发光）
            AddWhiteOutline(LockTimeHost, LockTimeText, radius: 2.5, opacity: 1.0);
            AddWhiteOutline(LockTimeHost, LockTimeText, radius: 5.0, opacity: 0.45);

            // 日期与星期同样加白色描边（与时间一致的两层叠加）
            AddWhiteOutline(FullDateHost, FullDateText, radius: 1.8, opacity: 1.0);
            AddWhiteOutline(FullDateHost, FullDateText, radius: 3.5, opacity: 0.45);
            AddWhiteOutline(DayOfWeekHost, DayOfWeekText, radius: 1.8, opacity: 1.0);
            AddWhiteOutline(DayOfWeekHost, DayOfWeekText, radius: 3.5, opacity: 0.45);

            // 本地模块：天气 / 降水每 10 分钟，值日每 30 分钟，课表每 10 分钟
            _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
            _weatherTimer.Start();

            _precipTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _precipTimer.Tick += async (_, _) => await RefreshPrecipAsync();
            _precipTimer.Start();

            _dutyTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _dutyTimer.Tick += async (_, _) => await RefreshDutyAsync();
            _dutyTimer.Start();

            _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _scheduleTimer.Tick += async (_, _) => await RefreshScheduleAsync();
            _scheduleTimer.Start();

            _homeworkRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _homeworkRefreshTimer.Tick += async (_, _) => await RefreshHomeworkAsync();
            _homeworkRefreshTimer.Start();

            _ = LoadModulesAsync();
            BindPagerPointerTracking();
            ApplyScreenSwitchArgument();
        }

        /// <summary>
        /// 分页手势：用 AddHandler(handledEventsToo: true) 在分页容器上统一接管指针事件
        /// （内部列表/卡片会把事件标记为已处理，普通事件处理收不到），
        /// **手指或鼠标左键横向拖动超过阈值 → 切换相邻屏**，触屏与鼠标都能用；
        /// 同时记录“指针是否按下”，用于区分用户滑动与内部控件焦点引起的自动滚动。
        /// </summary>
        private void BindPagerPointerTracking()
        {
            try
            {
                ScreenPager.AddHandler(UIElement.PointerPressedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPagerPointerPressed), true);
                ScreenPager.AddHandler(UIElement.PointerMovedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPagerPointerMoved), true);
                ScreenPager.AddHandler(UIElement.PointerReleasedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPagerPointerReleased), true);
                ScreenPager.AddHandler(UIElement.PointerCanceledEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPagerPointerReleased), true);
                ScreenPager.AddHandler(UIElement.PointerCaptureLostEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPagerPointerReleased), true);
            }
            catch { }
        }

        private void OnPagerPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _pagerPointerDown = true;
            _lastPagerPointer = DateTime.Now;
            _swipeTracking = false;

            try
            {
                // 起点落在滑块/输入框/列表上时不翻屏（避免与拖动进度条、音量、选时间打架）
                if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
                _swipeStart = e.GetCurrentPoint(ScreenPager).Position;
                _swipePointerId = e.Pointer.PointerId;
                _swipeTracking = true;
            }
            catch { }
        }

        private void OnPagerPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_swipeTracking || _swipePointerId != e.Pointer.PointerId) return;
            try
            {
                var p = e.GetCurrentPoint(ScreenPager).Position;
                double dx = p.X - _swipeStart.X;
                double dy = p.Y - _swipeStart.Y;

                // 横向位移达到阈值，且明显大于纵向 → 翻屏
                if (Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) < Math.Abs(dy) * 1.2) return;

                _swipeTracking = false;
                _lastSwipeAt = DateTime.Now;
                GoToScreen(_targetScreen + (dx < 0 ? 1 : -1));   // 左滑 → 下一屏
            }
            catch { }
        }

        private void OnPagerPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _pagerPointerDown = false;
            _lastPagerPointerUp = DateTime.Now;
            _swipeTracking = false;
        }

        /// <summary>指针起点是否落在“本身就要拖动/输入”的控件上（滑块、输入框、下拉框、列表、滚动条）。</summary>
        private static bool IsInteractiveSource(DependencyObject? source)
        {
            try
            {
                var el = source;
                while (el != null)
                {
                    if (el is Slider || el is TextBox || el is ComboBox || el is ListView) return true;
                    if (el is ScrollViewer) return false;    // 普通滚动区允许滑动翻屏
                    el = VisualTreeHelper.GetParent(el);
                }
            }
            catch { }
            return false;
        }


        /// <summary>诊断用：启动参数 /screen=N（1 起）可让程序直接停在第 N 屏，便于截图核对。</summary>
        private void ApplyScreenSwitchArgument()
        {
            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (!arg.StartsWith("/screen=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(arg.Substring("/screen=".Length), out int n)) continue;

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        GoToScreen(n - 1);
                    };
                    timer.Start();
                    return;
                }
            }
            catch { }
        }

        private void DesktopPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _desktopClockTimer?.Stop();
            _weatherTimer?.Stop();
            _precipTimer?.Stop();
            _dutyTimer?.Stop();
            _scheduleTimer?.Stop();
            _homeworkRefreshTimer?.Stop();
            StopHomeworkCarousel();
            _isPageReady = false;
        }

        private async Task LoadModulesAsync()
        {
            await LoadClassesAsync();
            await RefreshWeatherAsync();
            await RefreshPrecipAsync();
            await RefreshDutyAsync();
            await RefreshScheduleAsync();
            await RefreshHomeworkAsync();
        }

        // ==================== 屏幕分页（滑动切换） ====================

        private void BuildScreenDots()
        {
            try
            {
                if (PageDots.Children.Count > 0) return;

                PageDots.Children.Add(MakeArrowButton("‹", -1));
                for (int i = 0; i < ScreenCount; i++)
                {
                    int index = i;
                    var dot = new Border
                    {
                        Width = 12,
                        Height = 12,
                        CornerRadius = new CornerRadius(6),
                        Background = new SolidColorBrush(Color.FromArgb(0x88, 0x1A, 0x1A, 0x1A)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    dot.Tapped += (_, _) => GoToScreen(index);
                    PageDots.Children.Add(dot);
                }
                PageDots.Children.Add(MakeArrowButton("›", +1));
                UpdateScreenDots();
            }
            catch { }
        }

        private Button MakeArrowButton(string glyph, int delta)
        {
            var btn = new Button
            {
                Content = glyph,
                FontSize = 22,
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(22),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
                BorderThickness = new Thickness(0)
            };
            btn.Click += (_, _) => GoToScreen(_currentScreen + delta);
            return btn;
        }

        /// <summary>把当前屏告知宿主主窗口：只有第 1 屏（主页面）显示时灵动岛才常驻显示。</summary>
        private void NotifyScreenToHost(int index)
        {
            try { ParentWindow?.OnDesktopScreenChanged(index); } catch { }
        }
        /// <summary>切到第 index 屏（0 起，越界自动收敛）。</summary>
        private void GoToScreen(int index)
        {
            try
            {
                index = Math.Clamp(index, 0, ScreenCount - 1);
                _targetScreen = index;
                double pageWidth = HomeScreen.ActualWidth > 1 ? HomeScreen.ActualWidth : ScreenPager.ViewportWidth;
                if (pageWidth <= 1) return;
                ScreenPager.ChangeView(index * pageWidth, null, null, false);
                _currentScreen = index;
                UpdateScreenDots();
                MainWindow.IslandLog($"分页切换 → 第 {index + 1} 屏（偏移 {index * pageWidth:F0}）");
                NotifyScreenToHost(index);
            }
            catch { }
        }

        private void UpdateScreenDots()
        {
            try
            {
                // PageDots 结构：‹ + N 个圆点 + ›
                for (int i = 0; i < ScreenCount; i++)
                {
                    int childIndex = i + 1;
                    if (childIndex >= PageDots.Children.Count) break;
                    if (PageDots.Children[childIndex] is Border dot)
                        dot.Opacity = i == _currentScreen ? 1.0 : 0.45;
                }
            }
            catch { }
        }

        private void ScreenPager_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            try
            {
                double pageWidth = HomeScreen.ActualWidth > 1 ? HomeScreen.ActualWidth : ScreenPager.ViewportWidth;
                if (pageWidth <= 1) return;
                _screenWidth = pageWidth;

                int index = (int)Math.Round(ScreenPager.HorizontalOffset / pageWidth);
                index = Math.Clamp(index, 0, ScreenCount - 1);

                // 圆点跟随当前实际偏移
                if (index != _currentScreen)
                {
                    _currentScreen = index;
                    UpdateScreenDots();
                    NotifyScreenToHost(index);
                }

                // 拖动过程中（中间态）不纠偏：只要有明显位移就认为用户在操作，记下时间
                if (e.IsIntermediate)
                {
                    if (Math.Abs(ScreenPager.HorizontalOffset - _targetScreen * pageWidth) > pageWidth * 0.12)
                        _lastUserPanAt = DateTime.Now;
                    return;
                }

                // 拖动刚结束：以用户停在的那一屏为准
                if ((DateTime.Now - _lastUserPanAt).TotalSeconds < 0.8)
                {
                    _targetScreen = index;
                    return;
                }

                // 手指按住期间、或刚松手（吸附动画进行中）也不纠偏
                bool gesturing = _pagerPointerDown
                    || (DateTime.Now - _lastPagerPointerUp).TotalSeconds < 0.8
                    || (DateTime.Now - _lastSwipeAt).TotalSeconds < 0.8;
                if (gesturing) return;

                // 非用户操作却偏离目标屏（内部控件获得焦点触发的自动滚动）→ 纠偏回来
                if (index != _targetScreen)
                {
                    MainWindow.IslandLog($"分页纠偏：被带偏到第 {index + 1} 屏，回到第 {_targetScreen + 1} 屏");
                    ScreenPager.ChangeView(_targetScreen * pageWidth, null, null, true);
                }
            }
            catch { }
        }

        /// <summary>鼠标滚轮翻页：仅当指针不在内部可纵向滚动的列表/卡片上时才翻屏，避免抢内部滚动。</summary>
        private void ScreenPager_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (IsInsideInnerScrollViewer(e.OriginalSource as DependencyObject)) return;
                int delta = e.GetCurrentPoint(ScreenPager).Properties.MouseWheelDelta;
                if (delta == 0) return;
                GoToScreen(_currentScreen + (delta < 0 ? 1 : -1));
                e.Handled = true;
            }
            catch { }
        }

        private bool IsInsideInnerScrollViewer(DependencyObject? source)
        {
            try
            {
                var el = source;
                while (el != null)
                {
                    if (el is ScrollViewer sv && !ReferenceEquals(sv, ScreenPager)) return true;
                    el = VisualTreeHelper.GetParent(el);
                }
            }
            catch { }
            return false;
        }

        // ==================== 时钟 ====================

        private void UpdateDesktopClock(object? sender, object e)
        {
            if (!_isPageReady) return;
            var now = DateTime.Now;
            LockTimeText.Text = now.ToString("HH:mm");
            FullDateText.Text = $"{now.Month}月{now.Day}日";
            DayOfWeekText.Text = now.DayOfWeek.ToString();
        }

        /// <summary>灵动岛展开时桌面大字时间淡出，收起后淡入。</summary>
        public void SetClockFaded(bool faded)
        {
            try
            {
                var anim = new DoubleAnimation
                {
                    To = faded ? 0 : 1,
                    Duration = TimeSpan.FromMilliseconds(faded ? 220 : 320),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, ClockPanel);
                Storyboard.SetTargetProperty(anim, "Opacity");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch { }
        }

        /// <summary>
        /// 给文字加白色描边/外发光：WinUI 的 TextBlock 没有描边属性，
        /// 这里用多层“白色偏移副本”叠在主文字下方实现（副本文字通过绑定跟随主文字更新）。
        /// </summary>
        private static void AddWhiteOutline(Grid host, TextBlock source, double radius, double opacity)
        {
            try
            {
                (double X, double Y)[] offsets =
                {
                    (-1, -1), (0, -1), (1, -1),
                    (-1, 0),           (1, 0),
                    (-1, 1),  (0, 1),  (1, 1)
                };

                foreach (var (ox, oy) in offsets)
                {
                    var copy = new TextBlock
                    {
                        FontSize = source.FontSize,
                        FontWeight = source.FontWeight,
                        FontFamily = source.FontFamily,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        Opacity = opacity,
                        HorizontalAlignment = source.HorizontalAlignment,
                        VerticalAlignment = source.VerticalAlignment,
                        IsHitTestVisible = false,
                        RenderTransform = new TranslateTransform { X = ox * radius, Y = oy * radius }
                    };
                    copy.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
                    {
                        Source = source,
                        Path = new PropertyPath("Text"),
                        Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
                    });
                    host.Children.Insert(0, copy);   // 插到主文字下方
                }
            }
            catch { }
        }

        // ==================== 工具栏 ====================

        // 「刷新」→ 重新拉取天气 / 降水 / 值日 / 课表 / 班级，并立即检查一次新消息
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try { await RefreshAllAsync(); } catch { }
        }

        /// <summary>刷新主页所有本地化模块。</summary>
        private async Task RefreshAllAsync()
        {
            await LoadClassesAsync();
            await RefreshWeatherAsync();
            await RefreshPrecipAsync();
            await RefreshDutyAsync();
            await RefreshScheduleAsync();
            await RefreshHomeworkAsync();
            if (ParentWindow != null)
                await ParentWindow.PollNotificationsNowAsync();
        }

        // 「查看桌面」→ 直接显示真实桌面（最小化软件）
        private void DesktopExplorerButton_Click(object sender, RoutedEventArgs e) => ParentWindow?.ShowDesktop();

        // 「历史消息」→ 弹出灵动岛历史消息视图
        private void HistoryButton_Click(object sender, RoutedEventArgs e) => ParentWindow?.ShowIslandHistory();

        /// <summary>供主窗口/诊断开关调用：把音频文件加入点歌列表（与「选择文件…」同一路径）。</summary>
        public async Task<int> AddSongsAsync(IEnumerable<string> paths)
        {
            var added = await MusicScreen.AddPathsAsync(paths);
            if (added > 0) GoToScreen(2);          // 自动切到点歌屏，便于核对
            return added;
        }

        // ==================== 最近一条消息（大字） ====================

        /// <summary>由主窗口在轮询到新消息时调用：右下角大字显示最近一条消息的具体内容。</summary>
        public void SetLatestMessage(string sender, string content, DateTime time)
        {
            try
            {
                LatestMessageSenderText.Text = string.IsNullOrWhiteSpace(sender)
                    ? "班级通知" : $"来自{sender}老师";
                LatestMessageTimeText.Text = time.ToString("MM-dd HH:mm");
                LatestMessageText.Text = string.IsNullOrWhiteSpace(content) ? "（空消息）" : content;
            }
            catch { }
        }

        // ==================== 课表（今日 + 明日 并排两列） ====================

        /// <summary>
        /// 课表（数据来源与灵动岛一致：/class/api.php?action=get_schedule&amp;class_id=…）。
        /// 左侧区分成**两列**：今日课表 / 明日课表。
        /// 样式参考「今日值日」：节次 18px、课程 22px 加粗深蓝、老师 14px 灰（两列后每列更窄，字号做了自适应）。
        /// </summary>
        private async Task RefreshScheduleAsync()
        {
            try
            {
                ScheduleList.Children.Clear();
                TomorrowList.Children.Clear();

                string[] weekNames = { "日", "一", "二", "三", "四", "五", "六" };
                var today = DateTime.Now;
                var tomorrow = today.AddDays(1);
                ScheduleDayText.Text = $"{today.Month}月{today.Day}日 星期{weekNames[(int)today.DayOfWeek]}";
                TomorrowDayText.Text = $"{tomorrow.Month}月{tomorrow.Day}日 星期{weekNames[(int)tomorrow.DayOfWeek]}";

                int classId = ClassDataService.SelectedClassId;
                if (classId <= 0)
                {
                    ScheduleList.Children.Add(MakeSubText("请先选择班级"));
                    TomorrowList.Children.Add(MakeSubText("请先选择班级"));
                    return;
                }

                var all = await ClassDataService.GetScheduleAsync(classId);

                var todayLessons = ClassDataService.TodayLessons(all)
                    .Where(l => !string.IsNullOrWhiteSpace(l.Course) && l.Course != "未导入")
                    .OrderBy(l => l.LessonNo)
                    .ToList();
                var tomorrowLessons = ClassDataService.TomorrowLessons(all)
                    .Where(l => !string.IsNullOrWhiteSpace(l.Course) && l.Course != "未导入")
                    .OrderBy(l => l.LessonNo)
                    .ToList();

                RenderLessons(ScheduleList, todayLessons, "今日无课");
                RenderLessons(TomorrowList, tomorrowLessons, "明日无课");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[课表] " + ex.Message);
            }
        }

        /// <summary>把一节课表渲染到指定面板（两列共用，行宽按窄列自适应）。</summary>
        private static void RenderLessons(StackPanel target, List<LessonItem> lessons, string emptyHint)
        {
            try
            {
                if (lessons.Count == 0)
                {
                    target.Children.Add(MakeSubText(emptyHint));
                    return;
                }

                foreach (var lesson in lessons)
                {
                    var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });     // 节次
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 课程
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });    // 老师

                    var no = new TextBlock
                    {
                        Text = $"第{lesson.LessonNo}节",
                        FontSize = 18,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A))
                    };
                    var course = new TextBlock
                    {
                        Text = lesson.Course,
                        FontSize = 22,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x2A, 0x54))
                    };
                    var teacher = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(lesson.Teacher) ? "" : lesson.Teacher,
                        FontSize = 14,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        TextAlignment = TextAlignment.Right,
                        Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x1A, 0x1A, 0x1A))
                    };

                    Grid.SetColumn(course, 1);
                    Grid.SetColumn(teacher, 2);
                    row.Children.Add(no);
                    row.Children.Add(course);
                    row.Children.Add(teacher);
                    target.Children.Add(row);
                }
            }
            catch { }
        }

        private static TextBlock MakeSubText(string text) => new()
        {
            Text = text,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x1A, 0x1A, 0x1A))
        };

        // ==================== 班级选择（消息提醒用，不含消息内容） ====================

        private async Task LoadClassesAsync()
        {
            try
            {
                var classes = await ClassDataService.GetClassesAsync();
                ClassDataService.Load();
                int selected = ClassDataService.SelectedClassId;

                _classComboReady = false;
                ClassCombo.Items.Clear();
                foreach (var c in classes)
                    ClassCombo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });

                if (selected > 0)
                {
                    for (int i = 0; i < ClassCombo.Items.Count; i++)
                    {
                        if (ClassCombo.Items[i] is ComboBoxItem item && item.Tag is int id && id == selected)
                        {
                            ClassCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                _classComboReady = true;

                ClassHintText.Text = selected > 0
                    ? $"已选择班级 id = {selected}"
                    : "尚未选择班级：选择后才能接收班级消息与今日课表";
            }
            catch (Exception ex)
            {
                ClassHintText.Text = "班级列表加载失败";
                Debug.WriteLine(ex.Message);
            }
        }

        private async void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_classComboReady) return;
            if (ClassCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int id) return;

            ClassDataService.SetClass(id);       // 触发 ClassChanged
            ClassHintText.Text = $"已选择：{item.Content}（id = {id}）";

            await RefreshDutyAsync();
            await RefreshScheduleAsync();
            await RefreshHomeworkAsync();
            if (ParentWindow != null)
                await ParentWindow.PollNotificationsNowAsync();   // 立即检查该班新消息
        }

        // ==================== 天气 ====================

        private async Task RefreshWeatherAsync()
        {
            try
            {
                var w = await ClassDataService.GetWeatherAsync();
                CityButton.Content = $"{ClassDataService.CityName} ▾";
                if (w == null)
                {
                    WeatherDescText.Text = "天气获取失败";
                    return;
                }

                WeatherTempText.Text = string.IsNullOrWhiteSpace(w.Temp) ? "--°" : $"{w.Temp}°";
                WeatherDescText.Text = string.IsNullOrWhiteSpace(w.Weather) ? "--" : w.Weather;

                string range = "";
                if (!string.IsNullOrWhiteSpace(w.Low) && !string.IsNullOrWhiteSpace(w.High))
                    range = $"{w.Low}℃ ~ {w.High}℃";
                if (!string.IsNullOrWhiteSpace(w.TodayHint))
                    range += string.IsNullOrEmpty(range) ? w.TodayHint : $" · {w.TodayHint}";
                WeatherRangeText.Text = string.IsNullOrWhiteSpace(range) ? "--" : range;

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(w.Humidity)) parts.Add($"湿度 {w.Humidity}");
                if (!string.IsNullOrWhiteSpace(w.Wind) || !string.IsNullOrWhiteSpace(w.WindLevel))
                    parts.Add($"{w.Wind}{w.WindLevel}".Trim());
                if (!string.IsNullOrWhiteSpace(w.Aqi)) parts.Add($"AQI {w.Aqi}");
                if (!string.IsNullOrWhiteSpace(w.Rain24h)) parts.Add($"24h 降水 {w.Rain24h}mm");
                WeatherDetailText.Text = parts.Count == 0 ? "--" : string.Join(" · ", parts);

                WeatherUpdateText.Text = string.IsNullOrWhiteSpace(w.UpdateTime) ? "" : $"更新 {w.UpdateTime}";
                Debug.WriteLine($"[天气] {w.City} {w.Temp}° {w.Weather}");
            }
            catch { }
        }

        private async void CityButton_Click(object sender, RoutedEventArgs e)
        {
            var box = new TextBox
            {
                Text = ClassDataService.CityName,
                PlaceholderText = "输入城市名，如：金华",
                SelectionStart = 0
            };
            var dlg = new ContentDialog
            {
                Title = "切换城市",
                Content = box,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var found = await ClassDataService.SearchCityAsync(box.Text);
            if (found == null)
            {
                var fail = new ContentDialog
                {
                    Title = "未找到城市",
                    Content = $"没有查询到“{box.Text}”，请换个名称试试。",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await fail.ShowAsync();
                return;
            }

            ClassDataService.SetCity(found.Value.Id, found.Value.Name);
            await RefreshWeatherAsync();
            await RefreshPrecipAsync();
        }

        // ==================== 未来 2 小时降水 ====================

        private async Task RefreshPrecipAsync()
        {
            try
            {
                var p = await ClassDataService.GetPrecipAsync();
                if (p == null)
                {
                    PrecipSummaryText.Text = "降水数据获取失败";
                    return;
                }
                PrecipSummaryText.Text = p.Summary;
                BuildPrecipBars(p);
            }
            catch { }
        }

        /// <summary>24 根柱（每 5 分钟一根）显示未来 2 小时降水强度。</summary>
        private void BuildPrecipBars(MinutelyPrecip precip)
        {
            const int buckets = 24;      // 24 × 5 分钟 = 2 小时
            const int perBucket = 5;
            const double barMax = 44;

            PrecipBars.Children.Clear();
            PrecipBars.ColumnDefinitions.Clear();
            for (int i = 0; i < buckets; i++)
                PrecipBars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            double max = 0;
            var values = new double[buckets];
            for (int i = 0; i < buckets; i++)
            {
                double sum = 0;
                for (int j = 0; j < perBucket; j++)
                {
                    int idx = i * perBucket + j;
                    if (idx < precip.Values.Count) sum += precip.Values[idx];
                }
                values[i] = sum;
                max = Math.Max(max, sum);
            }
            if (max <= 0) max = 1;

            for (int i = 0; i < buckets; i++)
            {
                double ratio = values[i] / max;
                var bar = new Border
                {
                    Width = double.NaN,
                    Height = Math.Max(2, barMax * ratio),
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(values[i] > 0
                        ? Color.FromArgb(0xFF, 0x2B, 0x7D, 0xE9)
                        : Color.FromArgb(0x33, 0x2B, 0x7D, 0xE9))
                };
                Grid.SetColumn(bar, i);
                PrecipBars.Children.Add(bar);
            }
        }

        // ==================== 晚自习作业（逐条轮播） ====================


        /// <summary>
        /// 作业显示（数据源与大屏 dashboard 一致：get_homework）。
        /// 字号偏大（科目 24 / 内容 21）；**一屏显示不下时按条轮播**（默认 7 秒一条，右侧有 i/N 与圆点）。
        /// 正在进行的晚自习时段用左侧亮蓝色条高亮。
        /// </summary>
        private async Task RefreshHomeworkAsync()
        {
            try
            {
                int classId = ClassDataService.SelectedClassId;
                if (classId <= 0)
                {
                    _homework = new List<HomeworkItem>();
                    HomeworkDots.Children.Clear();
                    StopHomeworkCarousel();
                    ShowHomeworkItem(null, "请先选择班级");
                    return;
                }

                _homework = await ClassDataService.GetHomeworkAsync(classId);
                _homeworkIndex = 0;
                RenderHomework();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[作业] " + ex.Message);
            }
        }

        private void RenderHomework()
        {
            try
            {
                // 圆点：一条一个点（条数过多时只显示当前序号文字，避免圆点排太长）
                HomeworkDots.Children.Clear();
                if (_homework.Count > 1 && _homework.Count <= 12)
                {
                    for (int i = 0; i < _homework.Count; i++)
                    {
                        HomeworkDots.Children.Add(new Border
                        {
                            Width = 10,
                            Height = 10,
                            CornerRadius = new CornerRadius(5),
                            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x7D, 0xE9)),
                            Opacity = i == _homeworkIndex ? 1.0 : 0.35,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }
                }

                if (_homework.Count == 0)
                {
                    StopHomeworkCarousel();
                    ShowHomeworkItem(null, "今日暂无作业");
                    return;
                }

                if (_homeworkIndex < 0 || _homeworkIndex >= _homework.Count) _homeworkIndex = 0;
                ShowHomeworkItem(_homework[_homeworkIndex], null);

                if (_homework.Count > 1) StartHomeworkCarousel();   // 多条 → 轮播
                else StopHomeworkCarousel();
            }
            catch { }
        }

        private void ShowHomeworkItem(HomeworkItem? item, string? placeholder)
        {
            try
            {
                if (item == null)
                {
                    HomeworkSubjectText.Text = placeholder ?? "今日暂无作业";
                    HomeworkContentText.Text = "";
                    HomeworkNoteText.Text = "";
                    HomeworkNoteText.Visibility = Visibility.Collapsed;
                    HomeworkTimeText.Text = "";
                    HomeworkIndexText.Text = "";
                    HomeworkAccent.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x2B, 0x7D, 0xE9));
                    return;
                }

                HomeworkSubjectText.Text = string.IsNullOrWhiteSpace(item.Subject) ? "作业" : item.Subject;
                HomeworkContentText.Text = item.Content;
                HomeworkNoteText.Text = string.IsNullOrWhiteSpace(item.Note) ? "" : "备注：" + item.Note;
                HomeworkNoteText.Visibility = string.IsNullOrWhiteSpace(item.Note) ? Visibility.Collapsed : Visibility.Visible;
                HomeworkTimeText.Text = item.TimeDisplay;
                HomeworkIndexText.Text = _homework.Count > 1 ? $"{_homeworkIndex + 1}/{_homework.Count}" : "";

                // 正在进行的晚自习时段 → 亮蓝色条
                HomeworkAccent.Background = new SolidColorBrush(item.IsCurrentPeriod
                    ? Color.FromArgb(0xFF, 0x2B, 0x7D, 0xE9)
                    : Color.FromArgb(0x66, 0x2B, 0x7D, 0xE9));

                // 内容较长时自动降一档字号，尽量一屏显示（仍显示不下可滚动，或靠轮播逐条看）
                int len = (item.Content ?? "").Length;
                HomeworkContentText.FontSize = len <= 60 ? 21 : len <= 110 ? 18 : 16;
                HomeworkContentScroll.ChangeView(null, 0, null, true);   // 每条都从顶部开始显示

                // 圆点高亮
                for (int i = 0; i < HomeworkDots.Children.Count; i++)
                    if (HomeworkDots.Children[i] is Border dot) dot.Opacity = i == _homeworkIndex ? 1.0 : 0.35;
            }
            catch { }
        }

        private void StartHomeworkCarousel()
        {
            StopHomeworkCarousel();
            _homeworkCarouselTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            _homeworkCarouselTimer.Tick += (_, _) =>
            {
                if (_homework.Count <= 1) return;
                _homeworkIndex = (_homeworkIndex + 1) % _homework.Count;
                ShowHomeworkItem(_homework[_homeworkIndex], null);
            };
            _homeworkCarouselTimer.Start();
        }

        private void StopHomeworkCarousel()
        {
            try { _homeworkCarouselTimer?.Stop(); } catch { }
            _homeworkCarouselTimer = null;
        }

        // ==================== 今日值日 ====================

        private async Task RefreshDutyAsync()
        {
            try
            {
                DutyList.Children.Clear();
                string[] weekNames = { "日", "一", "二", "三", "四", "五", "六" };
                var now = DateTime.Now;
                DutyDayText.Text = $"{now.Month}月{now.Day}日 星期{weekNames[(int)now.DayOfWeek]}";

                int classId = ClassDataService.SelectedClassId;
                if (classId <= 0)
                {
                    DutyList.Children.Add(MakeSubText("请先在下方选择班级"));
                    return;
                }

                var all = await ClassDataService.GetDutyAsync(classId);
                var today = ClassDataService.TodayDuty(all);
                if (today.Count == 0)
                {
                    DutyList.Children.Add(MakeSubText("今日无值日安排"));
                    return;
                }

                foreach (var d in today)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    // 值日项目：20px；值日名单：24px（大屏可远距离看清）
                    var item = new TextBlock
                    {
                        Text = d.Item,
                        FontSize = 20,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A))
                    };
                    var people = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(d.People) ? "—" : d.People,
                        FontSize = 24,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x2A, 0x54))
                    };
                    Grid.SetColumn(people, 1);
                    row.Children.Add(item);
                    row.Children.Add(people);
                    DutyList.Children.Add(row);
                }
            }
            catch { }
        }
    }
}

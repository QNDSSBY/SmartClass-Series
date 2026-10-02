using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace SmartClassNight
{
    /// <summary>
    /// 灵动岛**独立窗口**的窗口层代码（视觉与动画逻辑在 DynamicIsland.cs）：
    ///
    /// - 置顶（IsAlwaysOnTop）+ 无边框 + 工具窗口 + 不接受激活（WS_EX_NOACTIVATE）：
    ///   因此**无论当前前台是哪个程序**（资源管理器、课件、浏览器…），收到消息都能看到灵动岛，
    ///   而且不会抢走焦点、不会出现在任务栏/Alt+Tab；
    /// - 窗口形状 = 当前灵动岛尺寸的圆角矩形（SetWindowRgn 裁剪），所以不需要透明窗口
    ///   也能得到胶囊/圆角卡片外形，窗口其余区域被直接裁掉；
    /// - 空闲（收起态）时整个窗口隐藏：屏幕上不显示任何信息与图案。
    /// </summary>
    public sealed partial class IslandWindow : Window
    {
        private const double IslandTopMargin = 8;   // 距屏幕顶部 8 DIP
        /// <summary>窗口（以及里面的黑色卡片）比胶囊本身每边各外扩的 DIP 数。</summary>
        private const double Overhang = 3;

        private AppWindow? _appWindow;
        private IntPtr _hwnd = IntPtr.Zero;
        private double _dpiScale = 1.0;
        private bool _shown;
        private bool _syncingSize;   // 正在把内容尺寸对齐到整像素（避免 SizeChanged 递归）

        public IslandWindow()
        {
            this.InitializeComponent();

            try
            {
                _hwnd = WindowNative.GetWindowHandle(this);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
                _appWindow = AppWindow.GetFromWindowId(id);

                if (_appWindow != null)
                {
                    if (_appWindow.Presenter is OverlappedPresenter presenter)
                    {
                        presenter.SetBorderAndTitleBar(false, false);
                        presenter.IsResizable = false;
                        presenter.IsMaximizable = false;
                        presenter.IsMinimizable = false;
                        presenter.IsAlwaysOnTop = true;      // 永远在最上层
                    }
                    _appWindow.IsShownInSwitchers = false;
                }

                NativeMethods.MakeToolWindowNoActivate(_hwnd);
                NativeMethods.RemoveSystemBorder(_hwnd);   // 去掉 Win11 系统边框（纯黑胶囊上它会显示成白边）
                _dpiScale = GetScale();
            }
            catch { }

            // 尺寸变化时同步窗口大小与裁剪区域
            IslandSize.SizeChanged += (_, _) => ApplyWindowGeometry();
            IslandRoot.Loaded += (_, _) =>
            {
                _dpiScale = GetScale();
                ApplyWindowGeometry();
            };
        }

        private double GetScale()
        {
            try
            {
                uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch { }
            return 1.0;
        }

        /// <summary>绑定宿主（主窗口）：用于顶部元素淡出、图片预览等回调，并告知屏幕尺寸。</summary>
        internal void Attach(MainWindow host, double screenWidth, double screenHeight)
        {
            _host = host;
            SetScreenSize(screenWidth, screenHeight);
        }

        /// <summary>屏幕尺寸（DIP）：灵动岛的横幅宽度/高度、历史方形边长都按它计算。</summary>
        internal void SetScreenSize(double width, double height)
        {
            try
            {
                if (width <= 1 || height <= 1) return;
                _screenWidth = width;
                _screenHeight = height;
                _dpiScale = GetScale();
                ApplyIslandMetrics();
            }
            catch { }
        }

        /// <summary>按当前 IslandSize 移动/缩放窗口，并裁剪成圆角矩形。</summary>
        internal void ApplyWindowGeometry()
        {
            try
            {
                if (_appWindow == null) return;

                double w = IslandSize.Width;
                double h = IslandSize.Height;
                if (double.IsNaN(w) || double.IsNaN(h) || w < 20 || h < 10) return;

                double scale = _dpiScale > 0 ? _dpiScale : 1.0;
                int pw = Math.Max(1, (int)Math.Round(w * scale));
                int ph = Math.Max(1, (int)Math.Round(h * scale));
                int over = Math.Max(2, (int)Math.Round(Overhang * scale));   // 窗口比胶囊每边各外扩 3 DIP

                // 窗口中心与胶囊中心一致（窗口外扩的部分用区域裁掉，不影响观感）
                int px = Math.Max(0, (int)Math.Round(Math.Max(0, (_screenWidth - w) / 2) * scale)) - over;
                int py = Math.Max(0, (int)Math.Round(IslandTopMargin * scale)) - over;

                _appWindow.MoveAndResize(new RectInt32(px, py, pw + over * 2, ph + over * 2));

                // 关键对齐：把 IslandSize（逻辑尺寸）换算成“正好等于整数物理像素”的 DIP 尺寸。
                // 150 DIP × 2.25 = 337.5px 与窗口 338px 之间的半像素误差会让边缘露出窗口白底
                // （就是肉眼看到的“灵动岛白边”）；黑色卡片本身在 XAML 里用 Margin="-3" 向外多画 3 DIP 盖满客户区。
                if (!_syncingSize)
                {
                    double snappedW = pw / scale;
                    double snappedH = ph / scale;
                    if (Math.Abs(IslandSize.Width - snappedW) > 0.01 || Math.Abs(IslandSize.Height - snappedH) > 0.01)
                    {
                        _syncingSize = true;
                        try
                        {
                            IslandSize.Width = snappedW;
                            IslandSize.Height = snappedH;
                        }
                        finally { _syncingSize = false; }
                    }
                }

                // 可见区域 = 胶囊本身（窗口内偏移 over），把窗口自带的系统边框裁掉
                double radius = Math.Min(IslandCornerRadius, h / 2);
                NativeMethods.ApplyRoundedRegionAt(_hwnd, over, over, pw, ph, (int)Math.Round(radius * scale));

                EnsureFramelessStyle();   // WinUI 会重新加回凸起边框样式，这里每次几何更新后确认清掉
            }
            catch { }
        }

        /// <summary>
        /// 确认窗口没有 WS_EX_WINDOWEDGE 等“立体边框”样式（否则纯黑胶囊边缘会出现 1px 白边/灰边）。
        /// WinUI 在 AppWindow.Show()/MoveAndResize 之后会把样式重新应用回来，所以需要反复确认。
        /// </summary>
        private void EnsureFramelessStyle()
        {
            try
            {
                if (_hwnd == IntPtr.Zero) return;
                long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
                long edge = NativeMethods.WS_EX_WINDOWEDGE | NativeMethods.WS_EX_CLIENTEDGE | NativeMethods.WS_EX_STATICEDGE;
                if ((ex & edge) == 0) return;

                NativeMethods.MakeToolWindowNoActivate(_hwnd);
                long after = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
                if (_styleLogCount < 2)
                {
                    _styleLogCount++;
                    MainWindow.IslandLog($"灵动岛窗口样式修正：0x{ex:X} → 0x{after:X}（去掉立体边框）");
                }
            }
            catch { }
        }

        private int _styleLogCount;

        /// <summary>显示灵动岛窗口（收到消息时调用）。</summary>
        internal void ShowIslandWindow()
        {
            try
            {
                _dpiScale = GetScale();
                ApplyWindowGeometry();

                if (_shown)
                {
                    ForceTopMost();
                    return;
                }

                _shown = true;
                try { _appWindow?.Show(); }
                catch { try { NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW); } catch { } }
                ApplyWindowGeometry();
                ForceTopMost();
            }
            catch { }
        }

        /// <summary>
        /// 强制把灵动岛压到最顶层（WS_EX_TOPMOST 层）。
        /// 主窗口是**全屏**窗口，仅靠 `IsAlwaysOnTop` 在个别情况下会被全屏窗口盖住，
        /// 这里每次显示都用 SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE) 再确认一次，
        /// 保证「主页面在前台时也能看到灵动岛」，同时不抢焦点。
        /// </summary>
        private void ForceTopMost()
        {
            try
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                NativeMethods.BringWindowToTop(_hwnd);
            }
            catch { }
        }

        /// <summary>点歌播放中：灵动岛左右拉长，左歌名、右进度时间。</summary>
        internal void ShowMusicInfo(string title, string timeText) => ShowInfo(title, timeText);

        /// <summary>打铃中：灵动岛左右拉长，左“正在播放铃声”、右任务名。</summary>
        internal void ShowBellInfo(string taskName) => ShowInfo("正在播放铃声", taskName);

        /// <summary>隐藏信息条，回到常驻胶囊。</summary>
        internal void HideInfoBar() => HideInfo();
        /// <summary>隐藏灵动岛窗口（消息显示完成、回到空闲态时调用）。</summary>
        internal void HideIslandWindow()
        {
            try
            {
                if (!_shown) return;
                _shown = false;
                try { _appWindow?.Hide(); }
                catch { try { NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE); } catch { } }
            }
            catch { }
        }
    }
}

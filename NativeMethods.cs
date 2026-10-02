using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartClassNight
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint WM_CLOSE = 0x0010;
        private const string CabinetWClass = "CabinetWClass";   // 资源管理器窗口类

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>枚举当前所有资源管理器（CabinetWClass）顶层窗口句柄。</summary>
        public static List<IntPtr> FindExplorerWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == CabinetWClass) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>获取窗口标题（无则返回空串）。</summary>
        public static string GetWindowTitle(IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return "";
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// 可靠地把窗口带到前台并激活（处理最小化、以及前台锁定导致 SetForegroundWindow 失效的情况）。
        /// </summary>
        public static void ActivateWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint appThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(hwnd, out _);

            bool attached = false;
            if (foreThread != appThread && foreThread != targetThread && targetThread != 0)
            {
                attached = AttachThreadInput(foreThread, appThread, true);
            }

            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            if (attached)
                AttachThreadInput(foreThread, appThread, false);
        }

        public const uint WM_GETICON = 0x007F;
        public const int ICON_SMALL = 0;
        public const int ICON_SMALL2 = 2;
        public const int GCLP_HICON = -14;
        public const int GCLP_HICONSM = -34;

        /// <summary>获取窗口的小图标句柄（WM_GETICON → 类图标兜底）。</summary>
        public static IntPtr GetWindowIconHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            IntPtr icon = SendMessage(hwnd, WM_GETICON, new IntPtr(ICON_SMALL2), IntPtr.Zero);
            if (icon == IntPtr.Zero) icon = SendMessage(hwnd, WM_GETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, GCLP_HICON);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, GCLP_HICONSM);
            return icon;
        }

        public const int GWL_EXSTYLE = -20;
        public const int GW_OWNER = 4;
        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_WINDOWEDGE = 0x00000100L;   // 凸起边框（会画成 1px 白边，灵动岛必须去掉）
        public const long WS_EX_CLIENTEDGE = 0x00000200L;   // 凹陷边框
        public const long WS_EX_STATICEDGE = 0x00020000L;   // 静态凹陷边框
        public const int GWL_STYLE = -16;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_NOZORDER = 0x0004;
        public const long WS_EX_NOACTIVATE = 0x08000000L;
        public const int SW_HIDE = 0;
        public const int SW_MAXIMIZE = 3;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        /// <summary>系统级窗口进程名：这些是系统 UI（输入法/操作中心/搜索等），不应出现在任务栏。</summary>
        private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "TextInputHost",          // Windows 输入体验（触摸键盘/输入法宿主）
            "ShellExperienceHost",    // Windows Shell Experience Host
            "SearchApp",              // Windows 搜索
            "LockApp",                // 锁屏
            "Widgets", "WidgetsWebView",  // Win11 小组件
            "StartMenuExperienceHost" // 开始菜单
        };

        private static bool IsExcludedProcess(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return ExcludedProcessNames.Contains(p.ProcessName);
            }
            catch { return false; }
        }

        /// <summary>
        /// 判断窗口是否应显示在任务栏（仿 Windows 任务栏规则）：
        /// 可见、无 owner（排除对话框/子窗口）、非工具窗口、非 WS_EX_NOACTIVATE。
        /// 注意：不要求 WS_EX_APPWINDOW —— 普通顶层窗口即使未设置该标志也会出现在任务栏。
        /// </summary>
        public static bool IsTaskbarWindow(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false;
            long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
            if ((exStyle & WS_EX_NOACTIVATE) != 0) return false;
            return true;
        }

        public static List<WindowInfo> GetOpenWindows()
        {
            var list = new List<WindowInfo>();
            uint currentPid = (uint)Environment.ProcessId;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsTaskbarWindow(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid) return true;   // 排除本程序自身窗口
                if (IsExcludedProcess(pid)) return true;  // 排除系统 UI 窗口

                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                StringBuilder sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                list.Add(new WindowInfo
                {
                    Hwnd = hWnd.ToInt64(),
                    Title = title,
                    ProcessId = pid
                });
                return true;
            }, IntPtr.Zero);

            return list;
        }

        // ==================== 文件系统图标（SHGetFileInfo）====================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        public const uint SHGFI_ICON = 0x000000100;        // 获取图标句柄
        public const uint SHGFI_LARGEICON = 0x000000000;   // 大图标（32×32 @96dpi）
        public const uint SHGFI_SMALLICON = 0x000000001;   // 小图标（16×16）
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010; // 不访问文件系统（用于虚拟路径）

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // ==================== 灵动岛独立窗口（置顶 / 无激活 / 异形裁剪）====================

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        /// <summary>
        /// 把窗口设为“置顶工具窗口 + 不接受激活”：不出现在任务栏/Alt+Tab，点击不会抢走前台焦点。
        /// 供灵动岛独立窗口使用（这样无论前台是谁，灵动岛都能显示而不打断用户操作）。
        ///
        /// 同时**清掉 WS_EX_WINDOWEDGE / WS_EX_CLIENTEDGE / WS_EX_STATICEDGE**：
        /// WinUI 的窗口默认带 WS_EX_WINDOWEDGE（“凸起边框”），系统会沿窗口画一条 1px 立体边
        /// （上/左浅色、下/右深色）—— 在纯黑灵动岛胶囊上就是肉眼看到的“白边”，必须去掉。
        /// </summary>
        public static void MakeToolWindowNoActivate(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                ex &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
                ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }
        }

        /// <summary>
        /// 用圆角矩形区域裁剪窗口形状（灵动岛胶囊/卡片不用透明窗口也能是圆角矩形，
        /// 其余区域直接被裁掉，无需依赖窗口透明）。
        /// 宽高与圆角半径使用**物理像素**。
        /// </summary>
        public static void ApplyRoundedRegion(IntPtr hwnd, int width, int height, int radius)
        {
            try
            {
                if (hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
                int r = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
                IntPtr rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, r * 2, r * 2);
                if (rgn == IntPtr.Zero) return;
                if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn);   // 成功后区域归系统所有
            }
            catch { }
        }

        /// <summary>
        /// 去掉窗口的系统边框（Windows 11 的 DWM 边框颜色属性，DWMWA_COLOR_NONE）。
        /// 灵动岛是异形小窗，系统默认那条 1px 边框在纯黑胶囊上会变成明显的“白边/灰边”。
        /// 同时关闭系统圆角（DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND），形状完全由我们自己的圆角裁剪决定。
        /// 老系统上调用失败无副作用。
        /// </summary>
        public static void RemoveSystemBorder(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;

                int noneColor = unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE
                DwmSetWindowAttribute(hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref noneColor, sizeof(int));

                int noRound = 1;                              // DWMWCP_DONOTROUND
                DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref noRound, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        /// <summary>
        /// 让窗口背景完全透明（Win32 未公开接口 SetWindowCompositionAttribute + ACCENT_ENABLE_TRANSPARENTGRADIENT）。
        /// 灵动岛窗口靠它做到「只有黑色胶囊、四周没有白边」：XAML 根节点是 Transparent，
        /// 窗口自身背景被清掉后，胶囊以外的区域直接透出桌面（圆角是 XAML 抗锯齿的真实圆角）。
        /// 失败也不影响使用（还有 ApplyRoundedRegion 圆角裁剪兜底）。
        /// </summary>
        public static void TryEnableWindowTransparency(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;

                // 1) 去掉窗口边框/非客户区的绘制（-1 = 整窗玻璃）
                try
                {
                    var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                }
                catch { }

                // 2) 强调色策略 = 透明渐变（alpha = 0 → 完全透明、无模糊、无着色）
                var accent = new AccentPolicy
                {
                    AccentState = AccentStateTransparentGradient,
                    AccentFlags = 2,
                    GradientColor = 0x00000000,
                    AnimationId = 0
                };
                int size = Marshal.SizeOf<AccentPolicy>();
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = WcaAccentPolicy,
                        Data = ptr,
                        SizeOfData = size
                    };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { }
        }

        private const int WcaAccentPolicy = 19;
        private const int AccentStateTransparentGradient = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        /// <summary>
        /// 在窗口内指定位置裁剪一个圆角矩形区域（区域从窗口左上角偏移 x,y）。
        /// 灵动岛窗口比胶囊本身四周各大 3 DIP，再用这个接口把可见区域定成胶囊本身，
        /// 这样窗口自带的 1px 立体边框（WS_EX_WINDOWEDGE，WinUI 不允许清除）会被裁掉，
        /// 不会在纯黑胶囊边缘露出白边/灰边。
        /// </summary>
        public static void ApplyRoundedRegionAt(IntPtr hwnd, int x, int y, int width, int height, int radius)
        {
            try
            {
                if (hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
                int r = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
                IntPtr rgn = CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, r * 2, r * 2);
                if (rgn == IntPtr.Zero) return;
                if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn);
            }
            catch { }
        }
        // ==================== 前台全屏检测（消息提醒抑制用） ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// 前台窗口是否是**别的程序的全屏窗口**（用于“前台有其他全屏页面时不显示消息通知”）。
        /// 判断依据：前台窗口不是本进程、窗口矩形基本铺满所在显示器、且不是桌面/任务栏等 Shell 窗口。
        /// </summary>
        public static bool IsForegroundOtherFullscreen()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;

                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)Environment.ProcessId) return false;    // 自己不算

                var cls = new StringBuilder(256);
                GetClassName(fg, cls, cls.Capacity);
                string className = cls.ToString();
                if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow")
                    return false;                                        // 桌面/任务栏等不算

                if (!GetWindowRect(fg, out RECT wr)) return false;
                IntPtr mon = MonitorFromWindow(fg, 2 /*MONITOR_DEFAULTTONEAREST*/);
                if (mon == IntPtr.Zero) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref mi)) return false;

                int mw = mi.rcMonitor.Width, mh = mi.rcMonitor.Height;
                int ww = wr.Width, wh = wr.Height;
                if (mw <= 0 || mh <= 0) return false;

                // 覆盖显示器 ≥98% 且基本对齐 → 视为全屏
                bool covers = ww >= mw * 0.98 && wh >= mh * 0.98;
                bool aligned = Math.Abs(wr.Left - mi.rcMonitor.Left) <= 4 && Math.Abs(wr.Top - mi.rcMonitor.Top) <= 4;
                return covers && aligned;
            }
            catch { return false; }
        }
        /// <summary>获取文件/文件夹/虚拟命名空间（如“此电脑” CLSID）的图标句柄。</summary>
        public static IntPtr GetShellIcon(string path, bool large = true)
        {
            if (string.IsNullOrEmpty(path)) return IntPtr.Zero;
            try
            {
                var info = new SHFILEINFO();
                uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
                IntPtr ok = SHGetFileInfo(path, 0, ref info,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), flags);
                return ok == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
            }
            catch { return IntPtr.Zero; }
        }
    }

    public class WindowInfo
    {
        public long Hwnd { get; set; }
        public string Title { get; set; } = "";
        public uint ProcessId { get; set; }
    }
}
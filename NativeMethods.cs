using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartClassNight
{
    public static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_APPWINDOW = 0x00040000L;
        public const int SW_RESTORE = 9;

        public static bool IsAltTabWindow(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;
            IntPtr hwndWalk = hWnd;
            while (hwndWalk != IntPtr.Zero)
            {
                hwndWalk = GetWindow(hwndWalk, 4); // GW_OWNER
                if (hwndWalk != IntPtr.Zero) return false;
            }
            long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
            if ((exStyle & WS_EX_APPWINDOW) == 0) return false;
            return true;
        }

        public static List<WindowInfo> GetOpenWindows()
        {
            var list = new List<WindowInfo>();
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsAltTabWindow(hWnd)) return true;

                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                StringBuilder sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
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
    }

    public class WindowInfo
    {
        public long Hwnd { get; set; }
        public string Title { get; set; } = "";
        public uint ProcessId { get; set; }
    }
}
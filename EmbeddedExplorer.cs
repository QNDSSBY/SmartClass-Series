using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SmartClassNight
{
    /// <summary>
    /// U 盘联动（1.1.8 起简化）：
    /// - 检测到**插入 U 盘**时：只做一件事 —— 用资源管理器打开该 U 盘（最大化由系统决定）；
    /// - 不隐藏本软件、不锁定界面、不跟踪资源管理器窗口；
    /// - **拔出 U 盘时不做任何动作**（不关窗口、不恢复界面）。
    /// 「开始 → 文件资源管理器」按钮仍可手动打开资源管理器。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly HashSet<string> _usbDrives = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _usbPollTimer;

        private void StartUsbAndExplorer()
        {
            _usbPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _usbPollTimer.Tick += (_, _) => PollUsbDrives();
            _usbPollTimer.Start();

            PollUsbDrives();
        }

        /// <summary>每 2 秒检查一次可移动磁盘：只处理“新插入”的盘（拔出不做任何事）。</summary>
        private void PollUsbDrives()
        {
            try
            {
                var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Removable) continue;
                    try { if (!d.IsReady) continue; } catch { continue; }
                    now.Add(Path.GetPathRoot(d.Name) ?? d.Name);
                }

                foreach (var root in now.Where(r => !_usbDrives.Contains(r)))
                    OpenUsbDrive(root);

                _usbDrives.Clear();
                foreach (var root in now) _usbDrives.Add(root);
            }
            catch { }
        }

        /// <summary>用资源管理器打开 U 盘（唯一的 U 盘动作）。</summary>
        private void OpenUsbDrive(string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                Debug.WriteLine($"[U盘] 检测到插入 {root}，打开资源管理器");
                MainWindow.IslandLog($"U盘插入：{root}（仅打开，不做其他动作）");
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>打开真实资源管理器（手动用途：开始菜单「文件资源管理器」）。</summary>
        public void OpenRealExplorer(string? root)
        {
            try
            {
                if (string.IsNullOrEmpty(root))
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
